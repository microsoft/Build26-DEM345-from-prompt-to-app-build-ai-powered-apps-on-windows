using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Runs YOLOS-small object detection using Windows ML (ONNX Runtime + NPU via EP catalog).
/// The model is bundled in Assets/Models and compiled for the best EP on first use.
/// </summary>
public sealed class OnnxModelRunner : IDisposable
{
    private OrtEnv? _ortEnv;
    private SessionOptions? _sessionOptions;
    private InferenceSession? _session;
    private bool _disposed;

    /// <summary>Human-readable backend label set after a successful load (e.g. "NPU (QNN)", "GPU", "CPU").</summary>
    public string ActiveBackend { get; private set; } = "Not loaded";

    // YOLOS-small input dimensions — model simplified with fixed 512×512 shape
    private const int InputWidth = 512;
    private const int InputHeight = 512;
    private const float DefaultConfidenceThreshold = 0.3f;

    private const string ModelFileName = "yolos-small.onnx";

    // COCO class labels indexed by original COCO category ID (with gaps as N/A).
    // Matches the model's id2label mapping from config.json.
    private static readonly string[] CocoLabels = [
        "N/A",           // 0  - padding
        "person",        // 1
        "bicycle",       // 2
        "car",           // 3
        "motorcycle",    // 4
        "airplane",      // 5
        "bus",           // 6
        "train",         // 7
        "truck",         // 8
        "boat",          // 9
        "traffic light", // 10
        "fire hydrant",  // 11
        "N/A",           // 12
        "stop sign",     // 13
        "parking meter", // 14
        "bench",         // 15
        "bird",          // 16
        "cat",           // 17
        "dog",           // 18
        "horse",         // 19
        "sheep",         // 20
        "cow",           // 21
        "elephant",      // 22
        "bear",          // 23
        "zebra",         // 24
        "giraffe",       // 25
        "N/A",           // 26
        "backpack",      // 27
        "umbrella",      // 28
        "N/A",           // 29
        "N/A",           // 30
        "handbag",       // 31
        "tie",           // 32
        "suitcase",      // 33
        "frisbee",       // 34
        "skis",          // 35
        "snowboard",     // 36
        "sports ball",   // 37
        "kite",          // 38
        "baseball bat",  // 39
        "baseball glove",// 40
        "skateboard",    // 41
        "surfboard",     // 42
        "tennis racket", // 43
        "bottle",        // 44
        "N/A",           // 45
        "wine glass",    // 46
        "cup",           // 47
        "fork",          // 48
        "knife",         // 49
        "spoon",         // 50
        "bowl",          // 51
        "banana",        // 52
        "apple",         // 53
        "sandwich",      // 54
        "orange",        // 55
        "broccoli",      // 56
        "carrot",        // 57
        "hot dog",       // 58
        "pizza",         // 59
        "donut",         // 60
        "cake",          // 61
        "chair",         // 62
        "couch",         // 63
        "potted plant",  // 64
        "bed",           // 65
        "N/A",           // 66
        "dining table",  // 67
        "N/A",           // 68
        "N/A",           // 69
        "toilet",        // 70
        "N/A",           // 71
        "tv",            // 72
        "laptop",        // 73
        "mouse",         // 74
        "remote",        // 75
        "keyboard",      // 76
        "cell phone",    // 77
        "microwave",     // 78
        "oven",          // 79
        "toaster",       // 80
        "sink",          // 81
        "refrigerator",  // 82
        "N/A",           // 83
        "book",          // 84
        "clock",         // 85
        "vase",          // 86
        "scissors",      // 87
        "teddy bear",    // 88
        "hair drier",    // 89
        "toothbrush"     // 90
    ];

    public bool IsModelLoaded => _session != null;

    /// <summary>
    /// Resolves the bundled model path from the app's install directory.
    /// Falls back to the LocalAppData cache if the bundled copy is absent
    /// (which happens on a fresh clone where the ~110 MB model wasn't shipped).
    /// </summary>
    private static string BundledModelPath
    {
        get
        {
            var bundled = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!,
                "Assets", "Models", ModelFileName);
            if (File.Exists(bundled)) return bundled;
            return ModelDownloader.GetCachedPath(ModelFileName);
        }
    }

    // Public mirror for Xenova's YOLOS-small ONNX export (transformers.js community).
    private const string YolosDownloadUrl =
        "https://huggingface.co/Xenova/yolos-small/resolve/main/onnx/model.onnx?download=true";

    /// <summary>
    /// Initializes the Windows ML environment, registers EPs, and loads the bundled model.
    /// Reports progress via the callback for UI updates. If the model is missing from
    /// the install directory, downloads it to LocalAppData first.
    /// </summary>
    public async Task InitializeAndLoadModelAsync(Action<string>? onStatus = null)
    {
        string modelPath = BundledModelPath;
        Log.Info($"Model path: {modelPath}");
        if (!File.Exists(modelPath))
        {
            onStatus?.Invoke("YOLOS model not bundled — downloading from Hugging Face (~110 MB, one-time)...");
            modelPath = await ModelDownloader.EnsureModelAsync(
                ModelFileName,
                YolosDownloadUrl,
                onProgress: (got, total, pct) =>
                {
                    if (total > 0)
                        onStatus?.Invoke($"Downloading YOLOS: {got / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB ({pct:F0}%)");
                });
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"YOLOS model not available after download attempt at {modelPath}");
        }

        long modelSize = new FileInfo(modelPath).Length;
        onStatus?.Invoke($"Model: {ModelFileName} ({modelSize / 1024.0 / 1024.0:F1} MB)");
        Log.Info($"Model: {ModelFileName} ({modelSize / 1024.0 / 1024.0:F1} MB)");

        // 1) Initialize ORT environment
        onStatus?.Invoke("Initializing Windows ML runtime...");
        try
        {
            await Task.Run(() =>
            {
                var envOptions = new EnvironmentCreationOptions
                {
                    logId = "ContosoStudio",
                    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
                };
                _ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"OrtEnv creation failed: {ex.Message}");
            onStatus?.Invoke($"OrtEnv creation failed: {ex.Message}");
        }

        // 2) Ensure + register certified EPs from Windows ML catalog (downloads NPU/GPU EPs on first run)
        try
        {
            onStatus?.Invoke("Discovering execution providers...");
            var catalog = ExecutionProviderCatalog.GetDefault();

            // Log what's known before registration
            try
            {
                foreach (var ep in catalog.FindAllProviders())
                {
                    onStatus?.Invoke($"  EP {ep.Name}: {ep.Certification}, {ep.ReadyState}");
                    Log.Info($"EP {ep.Name}: cert={ep.Certification} ready={ep.ReadyState} lib={ep.LibraryPath}");
                }
            }
            catch (Exception ex) { Log.Warn($"FindAllProviders: {ex.Message}"); }

            onStatus?.Invoke("Registering certified EPs (may download on first run)...");
            await catalog.EnsureAndRegisterCertifiedAsync();
            onStatus?.Invoke("EP registration complete");
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"⚠ EP registration failed: {ex.Message}");
            Log.Warn($"EP registration failed: {ex.Message}");
        }

        // 3) Inspect actually-available EP devices via ORT
        bool hasNpu = false, hasGpu = false;
        string? npuEpName = null, gpuEpName = null;
        try
        {
            var epDevices = _ortEnv?.GetEpDevices();
            if (epDevices != null)
            {
                foreach (var dev in epDevices)
                {
                    string epName = dev.EpName ?? "?";
                    var hwType = dev.HardwareDevice.Type;
                    onStatus?.Invoke($"  Device: {epName} → {hwType} ({dev.HardwareDevice.Vendor})");
                    Log.Info($"EP device: {epName} type={hwType} vendor={dev.HardwareDevice.Vendor}");
                    if (hwType == OrtHardwareDeviceType.NPU) { hasNpu = true; npuEpName ??= epName; }
                    else if (hwType == OrtHardwareDeviceType.GPU) { hasGpu = true; gpuEpName ??= epName; }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"GetEpDevices: {ex.Message}"); }

        // 4) Pick the best EP selection policy based on what's actually present
        _sessionOptions = new SessionOptions();
        string targetBackend;
        ExecutionProviderDevicePolicy? policy = null;
        if (hasNpu)
        {
            policy = ExecutionProviderDevicePolicy.PREFER_NPU;
            targetBackend = $"NPU ({npuEpName})";
        }
        else if (hasGpu)
        {
            policy = ExecutionProviderDevicePolicy.PREFER_GPU;
            targetBackend = $"GPU ({gpuEpName})";
        }
        else
        {
            targetBackend = "CPU";
        }

        if (policy.HasValue)
            _sessionOptions.SetEpSelectionPolicy(policy.Value);

        // 5) Try preferred backend first, then fall back to CPU if it fails
        onStatus?.Invoke($"Creating inference session targeting {targetBackend}...");
        Log.Info($"Creating session: target={targetBackend} policy={policy}");
        try
        {
            await Task.Run(() => _session = new InferenceSession(modelPath, _sessionOptions));
            ActiveBackend = targetBackend;
            Log.Info($"Session created on {targetBackend}");
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"⚠ {targetBackend} session failed: {ex.Message}");
            Log.Warn($"{targetBackend} session failed: {ex.Message}");
            _sessionOptions.Dispose();
            _sessionOptions = new SessionOptions(); // plain CPU
            try
            {
                onStatus?.Invoke("Falling back to plain CPU session...");
                await Task.Run(() => _session = new InferenceSession(modelPath, _sessionOptions));
                ActiveBackend = "CPU (fallback)";
                Log.Info("Session created on CPU fallback");
            }
            catch (Exception ex2)
            {
                Log.Error("CPU fallback session creation failed", ex2);
                throw new InvalidOperationException(
                    $"Failed to create inference session on both {targetBackend} and CPU: {ex2.Message}", ex2);
            }
        }

        onStatus?.Invoke($"✓ Active backend: {ActiveBackend}");
        Log.Info($"Model loaded — active backend: {ActiveBackend}");

        if (_session != null)
        {
            foreach (var input in _session.InputMetadata)
                onStatus?.Invoke($"  Input: {input.Key} [{string.Join("×", input.Value.Dimensions)}]");
            foreach (var output in _session.OutputMetadata)
                onStatus?.Invoke($"  Output: {output.Key} [{string.Join("×", output.Value.Dimensions)}]");
        }
    }

    /// <summary>
    /// Run object detection on sampled video frames.
    /// Extracts frames from the video and runs YOLOS inference on each.
    /// Returns detection results for all frames.
    /// </summary>
    public async Task<List<DetectionResult>> DetectObjectsAsync(
        string videoPath,
        int maxFrames = 30,
        float confidenceThreshold = DefaultConfidenceThreshold,
        Action<int, int>? onProgress = null,
        Action<string>? onDiagnostic = null)
    {
        if (_session == null)
            throw new InvalidOperationException("Model not loaded. Call InitializeAndLoadModelAsync first.");

        Log.Info($"DetectObjects: video={videoPath}, maxFrames={maxFrames}");

        // Extract frames on UI thread (Windows.Media.Editing requires STA)
        string framesDir = Path.Combine(Path.GetTempPath(), "ContosoStudio", "frames_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(framesDir);

        try
        {
            string[] framePaths = await ExtractFramesAsync(videoPath, framesDir, maxFrames);

            // Run inference on background thread to avoid blocking UI
            var results = await Task.Run(() =>
            {
                var allResults = new List<DetectionResult>();
                for (int i = 0; i < framePaths.Length; i++)
                {
                    onProgress?.Invoke(i + 1, framePaths.Length);
                    if (!File.Exists(framePaths[i])) continue;

                    // Recover original frame index from filename (frames may be skipped on extraction error)
                    int frameIdx = i;
                    var nameOnly = Path.GetFileNameWithoutExtension(framePaths[i]);
                    if (nameOnly.StartsWith("frame_") && int.TryParse(nameOnly[6..], out var parsedIdx))
                        frameIdx = parsedIdx;

                    var frameResults = RunDetectionOnFrame(framePaths[i], frameIdx, confidenceThreshold, onDiagnostic);
                    if (frameResults.Count > 0)
                    {
                        var best = frameResults.OrderByDescending(r => r.Confidence).First();
                        onDiagnostic?.Invoke($"  Frame {frameIdx + 1}: {frameResults.Count} det, top={best.Label} ({best.Confidence:P0})");
                    }
                    else
                    {
                        onDiagnostic?.Invoke($"  Frame {frameIdx + 1}: no detections");
                    }
                    allResults.AddRange(frameResults);
                }
                return allResults;
            });

            return results;
        }
        finally
        {
            try { Directory.Delete(framesDir, true); } catch { }
        }
    }

    /// <summary>
    /// Extracts evenly-spaced frames from a video using Windows.Media.Editing APIs.
    /// Each frame is saved as raw RGB bytes (InputWidth × InputHeight × 3).
    /// </summary>
    private static async Task<string[]> ExtractFramesAsync(string videoPath, string framesDir, int maxFrames)
    {
        var framePaths = new List<string>();

        Windows.Media.Editing.MediaComposition? composition = null;
        TimeSpan duration = TimeSpan.Zero;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            duration = clip.OriginalDuration;
        }
        catch (Exception ex)
        {
            Log.Warn($"ExtractFrames: could not open clip for {videoPath}: {ex.Message}");
            return [];
        }

        double intervalMs = duration.TotalMilliseconds / maxFrames;

        for (int i = 0; i < maxFrames; i++)
        {
            try
            {
                var timeOffset = TimeSpan.FromMilliseconds(i * intervalMs);
                var thumbnail = await composition.GetThumbnailAsync(
                    timeOffset, InputWidth, InputHeight,
                    Windows.Media.Editing.VideoFramePrecision.NearestFrame);

                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumbnail);
                var transform = new Windows.Graphics.Imaging.BitmapTransform
                {
                    ScaledWidth = (uint)InputWidth,
                    ScaledHeight = (uint)InputHeight,
                    InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                    transform,
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

                byte[] rgba = pixelData.DetachPixelData();

                byte[] rgb = new byte[InputWidth * InputHeight * 3];
                for (int p = 0; p < InputWidth * InputHeight; p++)
                {
                    rgb[p * 3] = rgba[p * 4];
                    rgb[p * 3 + 1] = rgba[p * 4 + 1];
                    rgb[p * 3 + 2] = rgba[p * 4 + 2];
                }

                string framePath = Path.Combine(framesDir, $"frame_{i:D4}.bin");
                await File.WriteAllBytesAsync(framePath, rgb);
                framePaths.Add(framePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"ExtractFrames: frame {i} failed: {ex.Message}");
                // Skip this frame — others may still succeed
            }
        }

        return [.. framePaths];
    }

    /// <summary>
    /// Runs YOLOS inference on a single frame and parses DETR-style output.
    /// </summary>
    private List<DetectionResult> RunDetectionOnFrame(string framePath, int frameIndex, float confidenceThreshold, Action<string>? onDiagnostic = null)
    {
        var results = new List<DetectionResult>();

        try
        {
            if (_session == null) return results;

            byte[] imageBytes = File.ReadAllBytes(framePath);
            var inputTensor = PreprocessImage(imageBytes);
            if (inputTensor == null) return results;

            // Bind input — YOLOS uses "pixel_values" as input name
            string inputName = _session.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var outputResults = _session.Run(inputs);

            // YOLOS outputs: "logits" [1, 100, 92] and "pred_boxes" [1, 100, 4]
            var logitsOutput = outputResults.FirstOrDefault(r => r.Name == "logits");
            var boxesOutput = outputResults.FirstOrDefault(r => r.Name == "pred_boxes");

            if (logitsOutput?.Value is DenseTensor<float> logits &&
                boxesOutput?.Value is DenseTensor<float> boxes)
            {
                results = ParseDetrDetections(logits, boxes, frameIndex, confidenceThreshold);
            }
            else
            {
                // Fallback: try generic output parsing for other model formats
                var output = outputResults.First();
                if (output.Value is DenseTensor<float> tensor)
                {
                    results = ParseGenericDetections(tensor, frameIndex, confidenceThreshold);
                }
            }
        }
        catch (Exception ex)
        {
            onDiagnostic?.Invoke($"    [diag] Frame {frameIndex} inference error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Preprocesses raw image bytes into a normalized input tensor [1, 3, H, W].
    /// Uses ImageNet normalization (mean=[0.485,0.456,0.406], std=[0.229,0.224,0.225]).
    /// </summary>
    private static DenseTensor<float>? PreprocessImage(byte[] imageBytes)
    {
        try
        {
            var tensor = new DenseTensor<float>([1, 3, InputHeight, InputWidth]);

            float[] mean = [0.485f, 0.456f, 0.406f];
            float[] std = [0.229f, 0.224f, 0.225f];

            int pixelCount = InputWidth * InputHeight;
            for (int i = 0; i < pixelCount && i * 3 + 2 < imageBytes.Length; i++)
            {
                int y = i / InputWidth;
                int x = i % InputWidth;
                tensor[0, 0, y, x] = (imageBytes[i * 3] / 255.0f - mean[0]) / std[0];
                tensor[0, 1, y, x] = (imageBytes[i * 3 + 1] / 255.0f - mean[1]) / std[1];
                tensor[0, 2, y, x] = (imageBytes[i * 3 + 2] / 255.0f - mean[2]) / std[2];
            }

            return tensor;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses DETR/YOLOS-style detections from logits and bounding box tensors.
    /// Applies softmax to logits and filters by confidence threshold.
    /// </summary>
    private static List<DetectionResult> ParseDetrDetections(
        DenseTensor<float> logits, DenseTensor<float> boxes, int frameIndex, float confidenceThreshold)
    {
        var results = new List<DetectionResult>();
        int numQueries = logits.Dimensions[1];
        int numClasses = logits.Dimensions[2];

        for (int q = 0; q < numQueries; q++)
        {
            // Apply softmax across all classes
            float maxLogit = float.MinValue;
            for (int c = 0; c < numClasses; c++)
            {
                float val = logits[0, q, c];
                if (val > maxLogit) maxLogit = val;
            }

            float sumExp = 0;
            for (int c = 0; c < numClasses; c++)
                sumExp += MathF.Exp(logits[0, q, c] - maxLogit);

            // Find best non-background, non-N/A class
            // Class 0 = "N/A" (padding), last class = "no object" background
            // Also skip gap classes (12, 26, 29, 30, 45, 66, 68, 69, 71, 83)
            int bestClassId = -1;
            float bestProb = 0;
            for (int c = 1; c < numClasses - 1; c++) // skip class 0 (N/A) and last (no-object)
            {
                // Skip N/A gap classes
                if (c < CocoLabels.Length && CocoLabels[c] == "N/A") continue;

                float prob = MathF.Exp(logits[0, q, c] - maxLogit) / sumExp;
                if (prob > bestProb)
                {
                    bestProb = prob;
                    bestClassId = c;
                }
            }

            if (bestProb < confidenceThreshold || bestClassId < 0) continue;

            // DETR boxes are (center_x, center_y, width, height) normalized to [0, 1]
            float cx = boxes[0, q, 0];
            float cy = boxes[0, q, 1];
            float w = boxes[0, q, 2];
            float h = boxes[0, q, 3];

            results.Add(new DetectionResult
            {
                FrameIndex = frameIndex,
                Label = bestClassId < CocoLabels.Length ? CocoLabels[bestClassId] : $"class_{bestClassId}",
                Confidence = bestProb,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h
            });
        }

        return results;
    }

    /// <summary>
    /// Fallback parser for generic YOLO-style detection output.
    /// </summary>
    private static List<DetectionResult> ParseGenericDetections(DenseTensor<float> output, int frameIndex, float confidenceThreshold)
    {
        var results = new List<DetectionResult>();
        int numDetections = output.Dimensions[1];
        int stride = output.Dimensions.Length > 2 ? output.Dimensions[2] : 6;

        for (int i = 0; i < numDetections && i < 100; i++)
        {
            float confidence = stride > 4 ? output[0, i, 4] : 0;
            if (confidence < confidenceThreshold) continue;

            int classId = 0;
            float maxClassProb = 0;
            for (int c = 5; c < stride && c - 5 < CocoLabels.Length; c++)
            {
                if (output[0, i, c] > maxClassProb)
                {
                    maxClassProb = output[0, i, c];
                    classId = c - 5;
                }
            }

            results.Add(new DetectionResult
            {
                FrameIndex = frameIndex,
                Label = classId < CocoLabels.Length ? CocoLabels[classId] : $"class_{classId}",
                Confidence = confidence * maxClassProb,
                X = output[0, i, 0],
                Y = output[0, i, 1],
                Width = output[0, i, 2],
                Height = output[0, i, 3]
            });
        }

        return results;
    }

    /// <summary>
    /// Generate placeholder detection results for demo purposes
    /// when the model is not available or inference fails.
    /// </summary>
    public static List<DetectionResult> GenerateDemoDetections(int frameCount)
    {
        var random = new Random(42);
        var results = new List<DetectionResult>();

        for (int i = 0; i < frameCount; i++)
        {
            int objectCount = random.Next(1, 4);
            for (int j = 0; j < objectCount; j++)
            {
                results.Add(new DetectionResult
                {
                    FrameIndex = i,
                    Label = CocoLabels[random.Next(CocoLabels.Length)],
                    Confidence = 0.6f + (float)random.NextDouble() * 0.35f,
                    X = (float)random.NextDouble() * 0.6f,
                    Y = (float)random.NextDouble() * 0.6f,
                    Width = 0.1f + (float)random.NextDouble() * 0.3f,
                    Height = 0.1f + (float)random.NextDouble() * 0.3f
                });
            }
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        _sessionOptions?.Dispose();
        _ortEnv?.Dispose();
    }
}
