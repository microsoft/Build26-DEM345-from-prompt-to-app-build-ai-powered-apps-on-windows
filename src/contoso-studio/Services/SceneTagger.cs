using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoStudio.Services;

/// <summary>
/// Per-frame scene/object classification using google/vit-base-patch16-224
/// (ImageNet-1k, 1000 classes). Returns the top-K class labels per frame.
/// </summary>
public sealed class SceneTagger : IDisposable
{
    private InferenceSession? _session;
    private string[]? _labels;
    public string ActiveBackend { get; private set; } = "Not loaded";
    public bool IsLoaded => _session != null;

    private const int InputSize = 224;
    private const string ModelFileName = "vit-base-patch16-224.onnx";
    private const string DownloadUrl =
        "https://huggingface.co/Xenova/vit-base-patch16-224/resolve/main/onnx/model.onnx?download=true";
    private const string LabelsFileName = "imagenet1k-labels.txt";

    public async Task InitializeAsync(Action<string>? onStatus = null)
    {
        if (_session != null) return;

        string modelPath = ModelDownloader.GetCachedPath(ModelFileName);
        if (!File.Exists(modelPath))
        {
            onStatus?.Invoke($"Downloading {ModelFileName} (~346 MB, one-time)...");
            modelPath = await ModelDownloader.EnsureModelAsync(ModelFileName, DownloadUrl,
                onProgress: (got, total, pct) =>
                {
                    if (total > 0)
                        onStatus?.Invoke($"Download: {got / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB ({pct:F0}%)");
                });
        }

        _labels = LoadLabels(onStatus);

        onStatus?.Invoke("Creating inference session...");
        var (session, backend) = await WindowsMlSessionFactory.CreateSessionAsync(modelPath, onStatus, forceCpu: true);
        _session = session;
        ActiveBackend = backend;
        onStatus?.Invoke($"✓ Loaded on {ActiveBackend}");
    }

    private static string[] LoadLabels(Action<string>? onStatus)
    {
        // Try bundled labels file (Assets/Models/imagenet1k-labels.txt)
        try
        {
            var bundled = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!,
                "Assets", "Models", LabelsFileName);
            if (File.Exists(bundled))
                return File.ReadAllLines(bundled).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }
        catch { }
        // Fallback: numeric labels (better than nothing)
        onStatus?.Invoke("⚠ ImageNet labels file missing — using numeric class IDs");
        return Enumerable.Range(0, 1000).Select(i => $"class_{i}").ToArray();
    }

    public async Task<List<SceneTag>> ClassifyAsync(
        IReadOnlyList<(int frameIndex, byte[] rgbaBytes, int width, int height)> frames,
        int topK = 3,
        Action<int, int>? onProgress = null,
        Action<string>? onDiagnostic = null)
    {
        if (_session == null) throw new InvalidOperationException("Call InitializeAsync first.");
        var results = new List<SceneTag>();

        await Task.Run(() =>
        {
            for (int i = 0; i < frames.Count; i++)
            {
                onProgress?.Invoke(i + 1, frames.Count);
                try
                {
                    var (idx, rgba, w, h) = frames[i];
                    var input = PreprocessRgba(rgba, w, h);
                    string inputName = _session.InputMetadata.Keys.First();
                    using var outs = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
                    var logitsOut = outs.First();
                    if (logitsOut.Value is DenseTensor<float> logits)
                    {
                        var topResults = TopK(logits, topK);
                        foreach (var (classId, prob) in topResults)
                        {
                            string label = (_labels != null && classId < _labels.Length) ? _labels[classId] : $"class_{classId}";
                            results.Add(new SceneTag
                            {
                                FrameIndex = idx,
                                ClassId = classId,
                                Label = label,
                                Confidence = prob
                            });
                        }
                        if (topResults.Count > 0)
                        {
                            var best = topResults[0];
                            string bestLabel = (_labels != null && best.classId < _labels.Length) ? _labels[best.classId] : $"class_{best.classId}";
                            onDiagnostic?.Invoke($"  Frame {idx + 1}: {bestLabel} ({best.prob:P0})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"    [diag] frame {i} classify failed: {ex.Message}");
                }
            }
        });

        return results;
    }

    private static DenseTensor<float> PreprocessRgba(byte[] rgba, int srcW, int srcH)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        float[] mean = { 0.5f, 0.5f, 0.5f }; // ViT uses 0.5/0.5 normalization
        float[] std = { 0.5f, 0.5f, 0.5f };
        for (int y = 0; y < InputSize; y++)
        {
            int sy = (int)((long)y * srcH / InputSize);
            for (int x = 0; x < InputSize; x++)
            {
                int sx = (int)((long)x * srcW / InputSize);
                int p = (sy * srcW + sx) * 4;
                if (p + 2 >= rgba.Length) continue;
                tensor[0, 0, y, x] = (rgba[p] / 255f - mean[0]) / std[0];
                tensor[0, 1, y, x] = (rgba[p + 1] / 255f - mean[1]) / std[1];
                tensor[0, 2, y, x] = (rgba[p + 2] / 255f - mean[2]) / std[2];
            }
        }
        return tensor;
    }

    private static List<(int classId, float prob)> TopK(DenseTensor<float> logits, int k)
    {
        // logits shape: [1, 1000]
        int n = logits.Dimensions[1];
        // softmax
        float maxL = float.MinValue;
        for (int i = 0; i < n; i++) if (logits[0, i] > maxL) maxL = logits[0, i];
        float sumExp = 0;
        var exps = new float[n];
        for (int i = 0; i < n; i++) { exps[i] = MathF.Exp(logits[0, i] - maxL); sumExp += exps[i]; }
        var probs = exps.Select((e, i) => (i, e / sumExp)).ToList();
        return probs.OrderByDescending(p => p.Item2).Take(k).ToList();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}

public sealed class SceneTag
{
    public int FrameIndex { get; init; }
    public int ClassId { get; init; }
    public string Label { get; init; } = "";
    public float Confidence { get; init; }
}
