using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Hardware preference for an inference step.
/// </summary>
public enum HardwarePreference
{
    Auto,
    NPU,
    GPU,
    CPU
}

/// <summary>
/// Whisper model size variant.
/// </summary>
public enum WhisperModelSize
{
    Tiny,
    Small,
    Medium
}

/// <summary>
/// Runs Whisper speech-to-text via ONNX Runtime + Windows ML.
/// Uses the Olive-fused all-in-one ONNX model from khmyznikov/whisper-int8-cpu-ort.onnx
/// (encoder + decoder + beam search + mel preprocess all fused in one file).
/// WinML's GetCompiledModel re-targets the model for the chosen execution provider (NPU/GPU/CPU).
/// </summary>
public sealed class WhisperRunner : IDisposable
{
    public enum TaskType
    {
        Translate = 50358,    // Whisper special token for translate-to-English
        Transcribe = 50359
    }

    // Model URLs from khmyznikov/whisper-int8-cpu-ort.onnx — same models AI Dev Gallery uses
    private static readonly Dictionary<WhisperModelSize, (string FileName, string Url)> ModelCatalog = new()
    {
        [WhisperModelSize.Tiny] = (
            "whisper_tiny_int8.onnx",
            "https://huggingface.co/khmyznikov/whisper-int8-cpu-ort.onnx/resolve/main/whisper_tiny_int8_cpu_ort_1.18.0.onnx?download=true"),
        [WhisperModelSize.Small] = (
            "whisper_small_int8.onnx",
            "https://huggingface.co/khmyznikov/whisper-int8-cpu-ort.onnx/resolve/main/whisper_small_int8_cpu_ort_1.18.0.onnx?download=true"),
        [WhisperModelSize.Medium] = (
            "whisper_medium_int8.onnx",
            "https://huggingface.co/khmyznikov/whisper-int8-cpu-ort.onnx/resolve/main/whisper_medium_int8_cpu_ort_1.18.0.onnx?download=true"),
    };

    private static readonly int[] _minLength = [0];
    private static readonly int[] _maxLength = [448];
    private static readonly int[] _numBeams = [2];
    private static readonly int[] _numReturnSequences = [1];
    private static readonly float[] _lengthPenalty = [1.0f];
    private static readonly float[] _repetitionPenalty = [1.0f];

    // Whisper processes audio in 30-second windows (16 kHz × 30 s = 480 000 samples)
    private const double ChunkSeconds = 30.0;
    private const double ChunkOverlapSeconds = 0.5;
    private const int SampleRate = 16000;

    private OrtEnv? _ortEnv;
    private SessionOptions? _sessionOptions;
    private InferenceSession? _session;
    private bool _disposed;
    private WhisperModelSize _loadedSize;

    // Transcript cache — keyed on (fileName, fileSize, lastWriteUtc, modelSize, language, translate)
    private static readonly string TranscriptCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContosoStudio", "TranscriptCache");

    /// <summary>Human-readable backend label set after a successful load.</summary>
    public string ActiveBackend { get; private set; } = "Not loaded";

    /// <summary>
    /// Ensure the requested model is downloaded and an inference session is created on the best EP.
    /// </summary>
    public async Task InitializeAsync(
        WhisperModelSize modelSize,
        HardwarePreference hardware,
        Action<string>? onStatus = null,
        Action<long, long, double>? onDownloadProgress = null,
        CancellationToken ct = default)
    {
        if (_session != null && _loadedSize == modelSize)
        {
            onStatus?.Invoke($"Whisper {modelSize} already loaded on {ActiveBackend}");
            return;
        }

        DisposeSession();

        // 1) Download model if needed
        var (fileName, url) = ModelCatalog[modelSize];
        var modelPath = await ModelDownloader.EnsureModelAsync(
            fileName, url,
            onProgress: onDownloadProgress,
            onStatus: onStatus,
            ct: ct);

        // 2) Initialize OrtEnv
        try
        {
            await Task.Run(() =>
            {
                var envOptions = new EnvironmentCreationOptions
                {
                    logId = "ContosoStudio.Whisper",
                    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
                };
                _ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions);
            }, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"OrtEnv creation failed (may already exist): {ex.Message}");
        }

        // 3) Register certified EPs from Windows ML catalog
        try
        {
            onStatus?.Invoke("Discovering execution providers...");
            var catalog = ExecutionProviderCatalog.GetDefault();
            await catalog.EnsureAndRegisterCertifiedAsync();
            onStatus?.Invoke("EP registration complete");
        }
        catch (Exception ex)
        {
            Log.Warn($"EP registration failed: {ex.Message}");
        }

        // 4) Inspect available EPs and pick a policy based on user preference.
        // Whisper-int8 (CPU-ORT fused build from khmyznikov/whisper-int8-cpu-ort.onnx) is only
        // validated on QNN NPU + plain CPU. OpenVINO / VitisAI / DML hard-crash (0xC0000005)
        // at session-create or first-Run with this model's int8 ops + dynamic shapes — even
        // when the EP is just registered in the OrtEnv. So we strictly disable any non-QNN
        // accelerator (NPU or GPU) and fall back to CPU.
        bool hasNpu = false, hasGpu = false;
        string? npuEpName = null, gpuEpName = null;
        string? unsupportedAccelName = null;
        try
        {
            var epDevices = _ortEnv?.GetEpDevices();
            if (epDevices != null)
            {
                foreach (var dev in epDevices)
                {
                    var hwType = dev.HardwareDevice.Type;
                    bool isQnn = IsWhisperSupportedNpu(dev.EpName);
                    if (hwType == OrtHardwareDeviceType.NPU)
                    {
                        if (isQnn) { hasNpu = true; npuEpName ??= dev.EpName; }
                        else { unsupportedAccelName ??= dev.EpName; }
                    }
                    else if (hwType == OrtHardwareDeviceType.GPU)
                    {
                        // Whisper-int8 GPU is not validated on any EP — keep CPU.
                        unsupportedAccelName ??= dev.EpName;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"GetEpDevices: {ex.Message}"); }

        if (unsupportedAccelName != null && !hasNpu && !hasGpu)
        {
            var msg = $"Detected accelerator '{unsupportedAccelName}' is not validated for Whisper-int8 — using CPU.";
            onStatus?.Invoke($"⚠ {msg}");
            Log.Info(msg);
        }

        _sessionOptions = new SessionOptions();

        // Audio preprocessing ops (mel spectrogram, resampling) ship in ORT Extensions
        try { _sessionOptions.RegisterOrtExtensions(); }
        catch (Exception ex) { Log.Warn($"RegisterOrtExtensions failed: {ex.Message}"); }

        var (policy, targetBackend) = ResolvePolicy(hardware, hasNpu, hasGpu, npuEpName, gpuEpName);

        // For NPU: must explicitly append the EP and compile the model — policy alone leaves
        // the model with dynamic shapes that QNN can't handle. For GPU/CPU: policy is fine.
        // Auto resolves to NPU when available, so we route Auto→NPU through the explicit path too.
        bool wantNpu = (hardware == HardwarePreference.NPU)
                    || (hardware == HardwarePreference.Auto && hasNpu);
        bool useExplicitEp = wantNpu && hasNpu && npuEpName != null;
        string? explicitEpName = useExplicitEp ? npuEpName : null;
        string activeModelPath = modelPath;

        if (useExplicitEp && explicitEpName != null)
        {
            try
            {
                onStatus?.Invoke($"Appending EP '{explicitEpName}'...");
                AppendEpByName(_sessionOptions, explicitEpName);

                onStatus?.Invoke($"Compiling model for {explicitEpName} (this may take a moment on first run)...");
                string? compileError = null;
                var compiledPath = await Task.Run(() => CompileModelForEp(_sessionOptions, modelPath, explicitEpName, out compileError), ct);
                if (!string.IsNullOrEmpty(compiledPath))
                {
                    activeModelPath = compiledPath!;
                    onStatus?.Invoke($"✓ Compiled model ready: {Path.GetFileName(compiledPath)}");
                }
                else
                {
                    onStatus?.Invoke($"⚠ Compilation skipped/failed ({compileError ?? "no output"}) — using uncompiled model with EP partitioning");
                }
            }
            catch (Exception ex)
            {
                onStatus?.Invoke($"⚠ EP append/compile failed: {ex.Message} — falling back to policy-based selection");
                Log.Warn($"AppendExecutionProvider/GetCompiledModel failed: {ex.Message}");
                useExplicitEp = false;
                _sessionOptions.Dispose();
                _sessionOptions = new SessionOptions();
                try { _sessionOptions.RegisterOrtExtensions(); } catch { }
            }
        }

        if (!useExplicitEp && policy.HasValue)
            _sessionOptions.SetEpSelectionPolicy(policy.Value);

        // 5) Create session — try preferred EP, fall back to plain CPU on failure
        onStatus?.Invoke($"Creating Whisper session targeting {targetBackend}...");
        Log.Info($"Whisper init: model={modelSize} target={targetBackend} policy={policy} explicitEp={explicitEpName}");
        try
        {
            await Task.Run(() => _session = new InferenceSession(activeModelPath, _sessionOptions), ct);
            ActiveBackend = targetBackend;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"⚠ {targetBackend} session failed: {ex.Message}");
            Log.Warn($"Whisper {targetBackend} session failed: {ex.Message}");
            _sessionOptions.Dispose();
            _sessionOptions = new SessionOptions();
            try { _sessionOptions.RegisterOrtExtensions(); } catch { }

            try
            {
                onStatus?.Invoke("Falling back to plain CPU session...");
                await Task.Run(() => _session = new InferenceSession(modelPath, _sessionOptions), ct);
                ActiveBackend = "CPU (fallback)";
            }
            catch (Exception ex2)
            {
                Log.Error("Whisper CPU fallback failed", ex2);
                throw new InvalidOperationException(
                    $"Failed to create Whisper session on both {targetBackend} and CPU: {ex2.Message}", ex2);
            }
        }

        _loadedSize = modelSize;
        onStatus?.Invoke($"✓ Whisper {modelSize} ready on {ActiveBackend}");
        Log.Info($"Whisper loaded: size={modelSize} backend={ActiveBackend}");
    }

    private static void AppendEpByName(SessionOptions sessionOptions, string epName)
    {
        if (string.Equals(epName, "CPU", StringComparison.OrdinalIgnoreCase))
            return;

        var environment = OrtEnv.Instance();
        var epDevices = environment.GetEpDevices();
        var devices = epDevices.Where(d => string.Equals(d.EpName, epName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (devices.Count == 0)
            throw new InvalidOperationException($"No devices found for EP '{epName}'");

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(epName, "QNNExecutionProvider", StringComparison.OrdinalIgnoreCase))
        {
            options["htp_performance_mode"] = "high_performance";
            sessionOptions.AppendExecutionProvider(environment, devices, options);
        }
        else if (string.Equals(epName, "DmlExecutionProvider", StringComparison.OrdinalIgnoreCase))
        {
            // DML may have multiple devices that conflict; pick the first
            sessionOptions.AppendExecutionProvider(environment, new[] { devices[0] }, options);
        }
        else
        {
            sessionOptions.AppendExecutionProvider(environment, devices, options);
        }
    }

    private static string? CompileModelForEp(SessionOptions sessionOptions, string modelPath, string epName, out string? error)
    {
        error = null;
        // CPU EP doesn't support EPContext compilation; skip.
        if (string.Equals(epName, "CPU", StringComparison.OrdinalIgnoreCase))
            return null;

        var dir = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        var compiledPath = Path.Combine(dir, $"{stem}.{epName}.onnx");

        if (!File.Exists(compiledPath))
        {
            try
            {
                using var compilationOptions = new OrtModelCompilationOptions(sessionOptions);
                compilationOptions.SetInputModelPath(modelPath);
                compilationOptions.SetOutputModelPath(compiledPath);
                compilationOptions.CompileModel();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Warn($"Compile failed for {epName}: {ex.Message}");
                if (File.Exists(compiledPath))
                {
                    try { File.Delete(compiledPath); } catch { }
                }
                return null;
            }
        }

        if (File.Exists(compiledPath) && new FileInfo(compiledPath).Length > 0)
            return compiledPath;
        return null;
    }

    /// <summary>
    /// Returns true if the named NPU execution provider is known to work with the
    /// Whisper-int8 fused model. OpenVINO / VitisAI / DML are known to fail (native
    /// crash at session-create or first inference) for this specific model.
    /// </summary>
    private static bool IsWhisperSupportedNpu(string? epName)
    {
        if (string.IsNullOrEmpty(epName)) return false;
        return epName.StartsWith("QNN", StringComparison.OrdinalIgnoreCase);
    }

    private static (ExecutionProviderDevicePolicy? policy, string label) ResolvePolicy(
        HardwarePreference pref, bool hasNpu, bool hasGpu, string? npuEpName, string? gpuEpName)
    {
        return pref switch
        {
            HardwarePreference.NPU when hasNpu => (ExecutionProviderDevicePolicy.PREFER_NPU, $"NPU ({npuEpName})"),
            HardwarePreference.GPU when hasGpu => (ExecutionProviderDevicePolicy.PREFER_GPU, $"GPU ({gpuEpName})"),
            HardwarePreference.CPU => (ExecutionProviderDevicePolicy.PREFER_CPU, "CPU"),
            HardwarePreference.Auto when hasNpu => (ExecutionProviderDevicePolicy.MAX_PERFORMANCE, $"NPU ({npuEpName}) [auto]"),
            HardwarePreference.Auto when hasGpu => (ExecutionProviderDevicePolicy.MAX_PERFORMANCE, $"GPU ({gpuEpName}) [auto]"),
            HardwarePreference.NPU => (null, "CPU (NPU not available)"),
            HardwarePreference.GPU => (null, "CPU (GPU not available)"),
            _ => (null, "CPU")
        };
    }

    /// <summary>
    /// Returns a cached transcript if one exists for the given file + settings combo.
    /// </summary>
    public TranscriptResult? TryGetCachedTranscript(
        string videoPath, WhisperModelSize modelSize, string language, bool translate)
    {
        try
        {
            var hash = ComputeTranscriptCacheHash(videoPath, modelSize, language, translate);
            if (hash == null) return null;

            var cachePath = Path.Combine(TranscriptCacheDir, hash + ".json");
            if (!File.Exists(cachePath)) return null;

            var json = File.ReadAllText(cachePath);
            var dto = JsonSerializer.Deserialize<TranscriptCacheDto>(json);
            if (dto == null) return null;

            var segments = dto.Segments.Select(s => new TranscriptSegment
            {
                Start = s.Start,
                End = s.End,
                Text = s.Text
            }).ToList();

            return new TranscriptResult
            {
                Segments = segments,
                FullText = dto.FullText,
                DeviceUsed = dto.DeviceUsed + " (cached)",
                ElapsedMs = dto.ElapsedMs,
                AudioDurationSeconds = dto.AudioDurationSeconds
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Transcript cache read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saves a transcript result to disk cache.
    /// </summary>
    public void CacheTranscript(
        string videoPath, WhisperModelSize modelSize, string language, bool translate,
        TranscriptResult result)
    {
        Task.Run(() =>
        {
            try
            {
                var hash = ComputeTranscriptCacheHash(videoPath, modelSize, language, translate);
                if (hash == null) return;

                Directory.CreateDirectory(TranscriptCacheDir);
                var dto = new TranscriptCacheDto
                {
                    FullText = result.FullText,
                    DeviceUsed = result.DeviceUsed,
                    ElapsedMs = result.ElapsedMs,
                    AudioDurationSeconds = result.AudioDurationSeconds,
                    Segments = result.Segments.Select(s => new TranscriptSegmentDto
                    {
                        Start = s.Start,
                        End = s.End,
                        Text = s.Text
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                var cachePath = Path.Combine(TranscriptCacheDir, hash + ".json");
                File.WriteAllText(cachePath, json);
                Log.Info($"Transcript cached to {cachePath} ({json.Length / 1024}KB)");
            }
            catch (Exception ex)
            {
                Log.Warn($"Transcript cache write failed: {ex.Message}");
            }
        });
    }

    private static string? ComputeTranscriptCacheHash(
        string videoPath, WhisperModelSize modelSize, string language, bool translate)
    {
        var fi = new FileInfo(videoPath);
        if (!fi.Exists) return null;

        var keyString = $"{fi.Name}|{fi.Length}|{fi.LastWriteTimeUtc:O}|{modelSize}|{language}|{translate}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes);
    }

    private sealed class TranscriptCacheDto
    {
        public string FullText { get; set; } = "";
        public string DeviceUsed { get; set; } = "";
        public long ElapsedMs { get; set; }
        public double AudioDurationSeconds { get; set; }
        public List<TranscriptSegmentDto> Segments { get; set; } = [];
    }

    private sealed class TranscriptSegmentDto
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Transcribe (or translate) an audio file end-to-end with chunking for long-form audio.
    /// </summary>
    public async Task<TranscriptResult> TranscribeAsync(
        string audioFilePath,
        string language = "en",
        bool translate = false,
        bool includeTimestamps = true,
        Action<string>? onStatus = null,
        IProgress<double>? onProgress = null,
        CancellationToken ct = default)
    {
        try
        {
            return await TranscribeCoreAsync(audioFilePath, language, translate, includeTimestamps, onStatus, onProgress, ct);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex) when (!ActiveBackend.StartsWith("CPU"))
        {
            // QNN/DML EP failed during inference (e.g., shape mismatch). Recreate session on CPU and retry once.
            onStatus?.Invoke($"⚠ Inference failed on {ActiveBackend}: {ex.Message.Split('\n')[0]} — retrying on CPU...");
            Log.Warn($"Whisper inference failed on {ActiveBackend}, retrying on CPU: {ex.Message}");

            // Recreate session on plain CPU
            DisposeSession();
            _sessionOptions = new SessionOptions();
            try { _sessionOptions.RegisterOrtExtensions(); } catch { }

            var (fileName, url) = ModelCatalog[_loadedSize];
            var modelPath = await ModelDownloader.EnsureModelAsync(fileName, url, onStatus: onStatus, ct: ct);
            await Task.Run(() => _session = new InferenceSession(modelPath, _sessionOptions), ct);
            ActiveBackend = "CPU (fallback after EP failure)";
            onStatus?.Invoke($"✓ Whisper {_loadedSize} ready on {ActiveBackend}");

            return await TranscribeCoreAsync(audioFilePath, language, translate, includeTimestamps, onStatus, onProgress, ct);
        }
    }

    private async Task<TranscriptResult> TranscribeCoreAsync(
        string audioFilePath,
        string language,
        bool translate,
        bool includeTimestamps,
        Action<string>? onStatus,
        IProgress<double>? onProgress,
        CancellationToken ct)
    {
        if (_session == null)
            throw new InvalidOperationException("WhisperRunner not initialized. Call InitializeAsync first.");

        var totalSeconds = await AudioExtractor.ProbeDurationSecondsAsync(audioFilePath, ct);
        if (totalSeconds <= 0)
        {
            // Fallback: extract everything and measure from byte count
            var allBytes = await AudioExtractor.ExtractPcm16Mono16kAsync(audioFilePath, onStatus, ct);
            totalSeconds = allBytes.Length / 2.0 / SampleRate;
        }

        onStatus?.Invoke($"Audio length: {totalSeconds:F1}s");

        var sw = Stopwatch.StartNew();
        var segments = new List<TranscriptSegment>();
        var fullText = new StringBuilder();

        var task = translate ? TaskType.Translate : TaskType.Transcribe;
        var langCode = GetLangId(language);

        // For audio shorter than one chunk, single pass; otherwise stream chunks
        if (totalSeconds <= ChunkSeconds + 0.1)
        {
            var bytes = await AudioExtractor.ExtractPcm16Mono16kAsync(audioFilePath, onStatus, ct);
            var rawText = await RunInferenceAsync(bytes, langCode, task, includeTimestamps, ct);
            ParseSegments(rawText, timeOffset: 0, segments, fullText);
            onProgress?.Report(1.0);
        }
        else
        {
            double cursor = 0;
            int chunkIndex = 0;
            int totalChunks = (int)Math.Ceiling(totalSeconds / (ChunkSeconds - ChunkOverlapSeconds));
            while (cursor < totalSeconds)
            {
                ct.ThrowIfCancellationRequested();
                chunkIndex++;
                onStatus?.Invoke($"Transcribing chunk {chunkIndex}/{totalChunks} (t={cursor:F1}s)");
                var dur = Math.Min(ChunkSeconds, totalSeconds - cursor);
                var bytes = await AudioExtractor.ExtractPcm16Mono16kRangeAsync(audioFilePath, cursor, dur, ct);
                if (bytes.Length == 0) break;

                var rawText = await RunInferenceAsync(bytes, langCode, task, includeTimestamps, ct);
                ParseSegments(rawText, timeOffset: cursor, segments, fullText);

                cursor += ChunkSeconds - ChunkOverlapSeconds;
                onProgress?.Report(Math.Min(1.0, cursor / totalSeconds));
            }
        }

        sw.Stop();
        Log.Info($"Whisper transcribe done: {segments.Count} segments, {sw.ElapsedMilliseconds}ms, {totalSeconds:F1}s audio");

        return new TranscriptResult
        {
            Segments = segments,
            FullText = fullText.ToString().Trim(),
            DeviceUsed = ActiveBackend,
            ElapsedMs = sw.ElapsedMilliseconds,
            AudioDurationSeconds = totalSeconds
        };
    }

    private Task<string> RunInferenceAsync(byte[] pcmBytes, int langCode, TaskType task, bool includeTimestamps, CancellationToken ct)
    {
        // 16-bit PCM → float[-1,1]
        var floatSamples = new float[pcmBytes.Length / 2];
        for (int i = 0; i < floatSamples.Length; i++)
        {
            short sample = BitConverter.ToInt16(pcmBytes, i * 2);
            floatSamples[i] = sample / 32768.0f;
        }

        var audioTensor = new DenseTensor<float>(floatSamples, [1, floatSamples.Length]);
        var timestampsTensor = new DenseTensor<int>(new[] { includeTimestamps ? 1 : 0 }, [1]);

        // Whisper "decoder_input_ids" prefix: [<|startoftranscript|>=50258, <|lang|>, <|task|>]
        var decoderInputIds = new int[] { 50258, langCode, (int)task };
        var langAndModeTensor = new DenseTensor<int>(decoderInputIds, [1, 3]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_pcm", audioTensor),
            NamedOnnxValue.CreateFromTensor("min_length", new DenseTensor<int>(_minLength, [1])),
            NamedOnnxValue.CreateFromTensor("max_length", new DenseTensor<int>(_maxLength, [1])),
            NamedOnnxValue.CreateFromTensor("num_beams", new DenseTensor<int>(_numBeams, [1])),
            NamedOnnxValue.CreateFromTensor("num_return_sequences", new DenseTensor<int>(_numReturnSequences, [1])),
            NamedOnnxValue.CreateFromTensor("length_penalty", new DenseTensor<float>(_lengthPenalty, [1])),
            NamedOnnxValue.CreateFromTensor("repetition_penalty", new DenseTensor<float>(_repetitionPenalty, [1])),
            NamedOnnxValue.CreateFromTensor("logits_processor", timestampsTensor),
            NamedOnnxValue.CreateFromTensor("decoder_input_ids", langAndModeTensor)
        };

        return Task.Run(() =>
        {
            using var results = _session!.Run(inputs);
            return results[0].AsTensor<string>().GetValue(0) ?? string.Empty;
        }, ct);
    }

    // Whisper outputs text like: "<|0.00|> Hello world.<|3.42|><|3.42|> This is a test.<|6.10|>"
    // We parse <|t.tt|> tokens into segment boundaries.
    private static readonly Regex TimestampRegex = new(@"<\|(\d+(?:\.\d+)?)\|>", RegexOptions.Compiled);

    private static void ParseSegments(string rawText, double timeOffset, List<TranscriptSegment> segments, StringBuilder fullText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return;

        var matches = TimestampRegex.Matches(rawText);
        if (matches.Count < 2)
        {
            // No timestamps — treat as one big segment
            var clean = TimestampRegex.Replace(rawText, "").Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                segments.Add(new TranscriptSegment { Start = timeOffset, End = timeOffset, Text = clean });
                fullText.Append(clean).Append(' ');
            }
            return;
        }

        // Iterate timestamp pairs and pull text between them
        for (int i = 0; i < matches.Count - 1; i++)
        {
            var startMatch = matches[i];
            var endMatch = matches[i + 1];
            var textBetween = rawText.Substring(startMatch.Index + startMatch.Length,
                                                endMatch.Index - (startMatch.Index + startMatch.Length)).Trim();
            if (string.IsNullOrWhiteSpace(textBetween)) continue;

            var startSec = double.Parse(startMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var endSec = double.Parse(endMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            // Skip seam duplicates: if we already have a segment that ends at or after this absolute start, drop it
            double absoluteStart = timeOffset + startSec;
            double absoluteEnd = timeOffset + endSec;

            if (segments.Count > 0 && absoluteStart < segments[^1].End - 0.1)
                continue; // overlap — skip duplicate

            segments.Add(new TranscriptSegment { Start = absoluteStart, End = absoluteEnd, Text = textBetween });
            fullText.Append(textBetween).Append(' ');
        }
    }

    /// <summary>Map ISO language code (e.g. "en") to Whisper's special-token id.</summary>
    public static int GetLangId(string lang) =>
        LangIdMap.TryGetValue(lang, out var id) ? id : 50259; // default English

    private static readonly Dictionary<string, int> LangIdMap = new()
    {
        ["en"] = 50259, ["zh"] = 50258, ["de"] = 50261, ["es"] = 50262, ["ru"] = 50263,
        ["ko"] = 50264, ["fr"] = 50265, ["ja"] = 50266, ["pt"] = 50267, ["tr"] = 50268,
        ["pl"] = 50269, ["ca"] = 50270, ["nl"] = 50271, ["ar"] = 50272, ["sv"] = 50273,
        ["it"] = 50274, ["id"] = 50275, ["hi"] = 50276, ["fi"] = 50277, ["vi"] = 50278,
        ["he"] = 50279, ["uk"] = 50260, ["el"] = 50281, ["ms"] = 50282, ["cs"] = 50283,
        ["ro"] = 50284, ["da"] = 50285, ["hu"] = 50286, ["ta"] = 50287, ["no"] = 50288,
        ["th"] = 50289, ["bg"] = 50292
    };

    public static IReadOnlyList<string> SupportedLanguages => LangIdMap.Keys.OrderBy(k => k).ToList();

    private void DisposeSession()
    {
        _session?.Dispose(); _session = null;
        _sessionOptions?.Dispose(); _sessionOptions = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeSession();
        _disposed = true;
    }
}
