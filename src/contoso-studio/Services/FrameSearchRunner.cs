using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoStudio.Services;

/// <summary>
/// CLIP (openai/clip-vit-base-patch16) image+text similarity, used by the
/// <c>find-frames</c> effect. Loads two QNN-compiled NPU artifacts built via
/// the <c>winml</c> CLI (see <c>Assets/Models/clip-vit-base-patch16/</c>):
/// the vision encoder (224×224 RGB → 512-d) and the text encoder (77-token
/// int32 input_ids → 512-d). Cosine similarity is computed in C#.
/// </summary>
public sealed class FrameSearchRunner : IDisposable
{
    private InferenceSession? _visionSession;
    private InferenceSession? _textSession;
    private ClipTokenizer? _tokenizer;

    public string ActiveBackend { get; private set; } = "Not loaded";
    public bool IsLoaded => _visionSession != null && _textSession != null && _tokenizer != null;

    // Cached extracted frames keyed by (fileName, maxFrames, fileSize, lastWriteUtc).
    // Caches raw RGBA frames (not embeddings) so the NPU embedding step still runs
    // visibly on re-runs — only the slow frame extraction is skipped.
    private readonly object _cacheLock = new();
    private (string fileName, int maxFrames, long fileSize, DateTime lastWriteUtc)? _cacheKey;
    private IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)>? _cachedFrames;

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContosoStudio", "FrameCache");

    private const int InputSize = 224;
    // CLIP image normalization (from openai/CLIP preprocessing).
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public async Task InitializeAsync(Action<string>? onStatus = null)
    {
        if (IsLoaded) return;

        string baseDir = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!,
            "Assets", "Models", "clip-vit-base-patch16");

        // Pick the artifact pair that matches an EP actually registered on this machine.
        // QNN-compiled and OpenVINO-compiled artifacts are EPContext wrappers tied to
        // their target EP — loading the wrong one fails even with CPU fallback because
        // the EPContext node has no CPU implementation.
        var (visionDir, textDir, epLabel) = await PickArtifactDirsAsync(baseDir, onStatus);
        string visionPath = Path.Combine(visionDir, "model.onnx");
        string textPath = Path.Combine(textDir, "model.onnx");

        if (!File.Exists(visionPath) || !File.Exists(textPath))
            throw new FileNotFoundException(
                $"CLIP NPU artifacts not found. Expected at {baseDir}\\{Path.GetFileName(visionDir)} " +
                $"and {baseDir}\\{Path.GetFileName(textDir)}. " +
                "Rebuild with: winml build -c config_*.json -m openai/clip-vit-base-patch16 -o ...");

        onStatus?.Invoke("Loading CLIP tokenizer...");
        _tokenizer = ClipTokenizer.LoadBundled();

        onStatus?.Invoke($"Loading vision encoder (CLIP-ViT-B/16) on {epLabel}...");
        var (vSession, backend) = await WindowsMlSessionFactory.CreateSessionAsync(visionPath, onStatus);
        _visionSession = vSession;
        ActiveBackend = backend;

        onStatus?.Invoke($"Loading text encoder on {backend}...");
        var (tSession, _) = await WindowsMlSessionFactory.CreateSessionAsync(textPath, onStatus);
        _textSession = tSession;

        onStatus?.Invoke($"✓ CLIP ready on {backend}");
    }

    /// <summary>
    /// Picks the (image-encoder, text-encoder) folder pair that matches an EP
    /// registered on the local machine. EPContext-compiled artifacts are tied to
    /// their target EP: a QNN-compiled wrapper won't load on Intel/AMD/NVIDIA, and
    /// vice versa. We probe registered EPs (after Windows ML EP registration) and
    /// pick the first matching folder that exists on disk; falls back to the legacy
    /// QNN folder for back-compat.
    /// </summary>
    private static async Task<(string visionDir, string textDir, string epLabel)> PickArtifactDirsAsync(
        string baseDir, Action<string>? onStatus)
    {
        // Ensure certified EPs are registered before we probe.
        try { await WindowsMlSessionFactory.EnsureEpsRegisteredAsync(onStatus); }
        catch { /* probe via OrtEnv anyway */ }

        bool hasQnn = false, hasOpenVino = false;
        try
        {
            var env = WindowsMlSessionFactory.GetOrCreateEnv(onStatus);
            foreach (var dev in env.GetEpDevices() ?? Enumerable.Empty<OrtEpDevice>())
            {
                var name = dev.EpName ?? string.Empty;
                if (name.Contains("QNN", StringComparison.OrdinalIgnoreCase)) hasQnn = true;
                else if (name.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase)) hasOpenVino = true;
            }
        }
        catch (Exception ex) { onStatus?.Invoke($"EP probe failed: {ex.Message}"); }

        // Preference order: match an actually-registered EP first; only fall back to
        // a folder that exists on disk.
        var candidates = new List<(string suffix, string label, bool prefer)>
        {
            ("-openvino", "OpenVINO NPU", hasOpenVino),
            ("",          "QNN NPU",      hasQnn),
        };
        // First pass: prefer EP-matched + present on disk.
        foreach (var (suffix, label, prefer) in candidates)
        {
            if (!prefer) continue;
            var v = Path.Combine(baseDir, "image-encoder" + suffix);
            var t = Path.Combine(baseDir, "text-encoder"  + suffix);
            if (File.Exists(Path.Combine(v, "model.onnx")) && File.Exists(Path.Combine(t, "model.onnx")))
                return (v, t, label);
        }
        // Second pass: any folder that exists on disk (last-resort, e.g. dev machine
        // missing a matching EP — let CreateSessionAsync surface a useful error).
        foreach (var (suffix, label, _) in candidates)
        {
            var v = Path.Combine(baseDir, "image-encoder" + suffix);
            var t = Path.Combine(baseDir, "text-encoder"  + suffix);
            if (File.Exists(Path.Combine(v, "model.onnx")) && File.Exists(Path.Combine(t, "model.onnx")))
                return (v, t, label + " (no matching EP registered)");
        }
        // Nothing on disk — return the QNN paths so the caller's File.Exists check throws.
        return (Path.Combine(baseDir, "image-encoder"),
                Path.Combine(baseDir, "text-encoder"),
                "QNN NPU");
    }

    /// <summary>Run the text encoder on <paramref name="prompt"/> and L2-normalize the output.</summary>
    public Task<float[]> EmbedTextAsync(string prompt) => Task.Run(() =>
    {
        if (_textSession == null || _tokenizer == null)
            throw new InvalidOperationException("Call InitializeAsync first.");

        var (ids, mask) = _tokenizer.Tokenize(prompt);
        // The QNN-compiled text encoder expects int32 input_ids and attention_mask of shape [1, 77].
        var idsTensor = new DenseTensor<int>(ids, [1, ClipTokenizer.MaxLen]);
        var maskTensor = new DenseTensor<int>(mask, [1, ClipTokenizer.MaxLen]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        };
        using var outs = _textSession.Run(inputs);
        var embeds = outs.First(o => o.Name == "text_embeds").AsTensor<float>();
        return Normalize(embeds.ToArray());
    });

    /// <summary>Run the vision encoder on a single resized 224×224 RGBA frame.</summary>
    public float[] EmbedFrame(byte[] rgba, int width, int height)
    {
        if (_visionSession == null) throw new InvalidOperationException("Call InitializeAsync first.");
        var tensor = PreprocessRgba(rgba, width, height);
        var input = NamedOnnxValue.CreateFromTensor("pixel_values", tensor);
        using var outs = _visionSession.Run([input]);
        var embeds = outs.First(o => o.Name == "image_embeds").AsTensor<float>();
        return Normalize(embeds.ToArray());
    }

    /// <summary>
    /// Returns cached extracted frames if the video filename, frame count, file size,
    /// and last-write time all match a previous <see cref="CacheFrames"/> call.
    /// Checks in-memory first, then falls back to disk.
    /// </summary>
    public IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)>? TryGetCachedFrames(
        string videoPath, int maxFrames)
    {
        var fi = new FileInfo(videoPath);
        if (!fi.Exists) return null;
        var key = (fi.Name, maxFrames, fi.Length, fi.LastWriteTimeUtc);

        lock (_cacheLock)
        {
            if (_cacheKey != null && _cachedFrames != null && _cacheKey.Value == key)
                return _cachedFrames;
        }

        var diskResult = LoadFramesFromDisk(key);
        if (diskResult != null)
        {
            lock (_cacheLock)
            {
                _cacheKey = key;
                _cachedFrames = diskResult;
            }
        }
        return diskResult;
    }

    /// <summary>
    /// Stores extracted frames in both in-memory and disk cache.
    /// </summary>
    public void CacheFrames(
        string videoPath, int maxFrames,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> frames)
    {
        var fi = new FileInfo(videoPath);
        var key = (fi.Name, maxFrames, fi.Length, fi.LastWriteTimeUtc);
        var readOnly = frames is List<(int, byte[], int, int)> list ? list.AsReadOnly() : frames;
        lock (_cacheLock)
        {
            _cacheKey = key;
            _cachedFrames = readOnly;
        }
        Task.Run(() => SaveFramesToDisk(key, frames));
    }

    /// <summary>
    /// Embed every extracted frame and cache the results. Returns the list of
    /// (frameIndex, embedding) pairs. Only caches when all frames embed successfully.
    /// </summary>
    public Task<IReadOnlyList<(int frameIndex, float[] embedding)>> EmbedFramesAsync(
        string videoPath, int maxFrames,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> frames,
        Action<int, int>? onProgress = null,
        Action<string>? onDiagnostic = null)
        => Task.Run(() =>
    {
        var embeddings = new List<(int, float[])>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            onProgress?.Invoke(i + 1, frames.Count);
            try
            {
                var (idx, rgba, w, h) = frames[i];
                var emb = EmbedFrame(rgba, w, h);
                embeddings.Add((idx, emb));
                onDiagnostic?.Invoke($"  Frame {idx + 1}: embedded (dim={emb.Length})");
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"  [warn] frame {i} embed failed: {ex.Message}");
            }
        }

        return (IReadOnlyList<(int frameIndex, float[] embedding)>)embeddings;
    });

    /// <summary>
    /// Score pre-computed image embeddings against a text embedding. Instant (CPU dot products).
    /// </summary>
    public List<(int frameIndex, float score)> ScoreEmbeddings(
        float[] textEmbed,
        IReadOnlyList<(int frameIndex, float[] embedding)> embeddings,
        Action<string>? onDiagnostic = null)
    {
        var results = new List<(int, float)>(embeddings.Count);
        foreach (var (idx, emb) in embeddings)
        {
            float sim = Dot(textEmbed, emb);
            results.Add((idx, sim));
            onDiagnostic?.Invoke($"  Frame {idx + 1}: similarity {sim:F3}");
        }
        return results;
    }

    /// <summary>
    /// Score every frame against <paramref name="textEmbed"/> (which must already be L2-normalized).
    /// Returns parallel arrays: source frame indices and cosine-similarity scores.
    /// </summary>
    public Task<List<(int frameIndex, float score)>> ScoreFramesAsync(
        float[] textEmbed,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> frames,
        Action<int, int>? onProgress = null,
        Action<string>? onDiagnostic = null)
        => Task.Run(() =>
    {
        var results = new List<(int, float)>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            onProgress?.Invoke(i + 1, frames.Count);
            try
            {
                var (idx, rgba, w, h) = frames[i];
                var img = EmbedFrame(rgba, w, h);
                float sim = Dot(textEmbed, img);
                results.Add((idx, sim));
                onDiagnostic?.Invoke($"  Frame {idx + 1}: similarity {sim:F3}");
            }
            catch (Exception ex)
            {
                onDiagnostic?.Invoke($"  [warn] frame {i} embed failed: {ex.Message}");
            }
        }
        return results;
    });

    private static DenseTensor<float> PreprocessRgba(byte[] rgba, int srcW, int srcH)
    {
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (int y = 0; y < InputSize; y++)
        {
            int sy = (int)((long)y * srcH / InputSize);
            for (int x = 0; x < InputSize; x++)
            {
                int sx = (int)((long)x * srcW / InputSize);
                int p = (sy * srcW + sx) * 4;
                if (p + 2 >= rgba.Length) continue;
                tensor[0, 0, y, x] = (rgba[p] / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (rgba[p + 1] / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (rgba[p + 2] / 255f - Mean[2]) / Std[2];
            }
        }
        return tensor;
    }

    private static float[] Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * (double)v[i];
        float inv = sumSq > 0 ? (float)(1.0 / Math.Sqrt(sumSq)) : 1f;
        var n = new float[v.Length];
        for (int i = 0; i < v.Length; i++) n[i] = v[i] * inv;
        return n;
    }

    private static float Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double s = 0;
        for (int i = 0; i < n; i++) s += a[i] * (double)b[i];
        return (float)s;
    }

    // ── Disk cache I/O (GZip-compressed RGBA frames) ──────────────────
    // Binary format (inside GZip): [4B magic "FRCV"][4B version=1][4B maxFrames]
    //   [8B fileSize][8B lastWriteUtc ticks][4B frameCount]
    //   per frame: [4B frameIndex][4B width][4B height][4B rgbaLen][rgbaLen bytes]

    private static readonly byte[] CacheMagic = "FRCV"u8.ToArray();
    private const int CacheVersion = 1;

    private static string GetCacheFilePath((string fileName, int maxFrames, long fileSize, DateTime lastWriteUtc) key)
    {
        var raw = Encoding.UTF8.GetBytes($"{key.fileName}|{key.maxFrames}|{key.fileSize}|{key.lastWriteUtc.Ticks}");
        var hash = SHA256.HashData(raw);
        return Path.Combine(CacheDir, Convert.ToHexString(hash)[..32] + ".frc");
    }

    private static void SaveFramesToDisk(
        (string fileName, int maxFrames, long fileSize, DateTime lastWriteUtc) key,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> frames)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var filePath = GetCacheFilePath(key);
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
            using var bw = new BinaryWriter(gz);

            bw.Write(CacheMagic);
            bw.Write(CacheVersion);
            bw.Write(key.maxFrames);
            bw.Write(key.fileSize);
            bw.Write(key.lastWriteUtc.Ticks);
            bw.Write(frames.Count);

            foreach (var (frameIndex, rgba, width, height) in frames)
            {
                bw.Write(frameIndex);
                bw.Write(width);
                bw.Write(height);
                bw.Write(rgba.Length);
                bw.Write(rgba);
            }
        }
        catch { /* best-effort */ }
    }

    private static IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)>? LoadFramesFromDisk(
        (string fileName, int maxFrames, long fileSize, DateTime lastWriteUtc) key)
    {
        try
        {
            var filePath = GetCacheFilePath(key);
            if (!File.Exists(filePath)) return null;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var br = new BinaryReader(gz);

            var magic = br.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(CacheMagic)) return null;

            int version = br.ReadInt32();
            if (version != CacheVersion) return null;

            int storedMaxFrames = br.ReadInt32();
            long storedFileSize = br.ReadInt64();
            long storedTicks = br.ReadInt64();

            if (storedMaxFrames != key.maxFrames ||
                storedFileSize != key.fileSize ||
                storedTicks != key.lastWriteUtc.Ticks)
                return null;

            int frameCount = br.ReadInt32();
            var frames = new List<(int, byte[], int, int)>(frameCount);
            for (int i = 0; i < frameCount; i++)
            {
                int frameIndex = br.ReadInt32();
                int width = br.ReadInt32();
                int height = br.ReadInt32();
                int rgbaLen = br.ReadInt32();
                var rgba = br.ReadBytes(rgbaLen);
                frames.Add((frameIndex, rgba, width, height));
            }
            return frames.AsReadOnly();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _visionSession?.Dispose();
        _textSession?.Dispose();
        _visionSession = null;
        _textSession = null;
    }
}

public sealed class FrameMatch
{
    public int FrameIndex { get; init; }
    public float Score { get; init; }
    public double TimestampSeconds { get; init; }

    public string TimestampLabel
    {
        get
        {
            var ts = System.TimeSpan.FromSeconds(System.Math.Max(0, TimestampSeconds));
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
    }

    public string ScoreLabel => $"{Score:0.000}";
}

public sealed class FrameMatchesResult
{
    public string Prompt { get; init; } = string.Empty;
    public System.Collections.Generic.IReadOnlyList<FrameMatch> Matches { get; init; } = System.Array.Empty<FrameMatch>();
    public string DeviceUsed { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public int FramesScanned { get; init; }
}
