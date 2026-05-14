using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoStudio.Services;

/// <summary>
/// Runs Depth-Anything-Small monocular depth estimation on sampled video frames.
/// Returns a per-frame 2D depth map (normalized to [0,1]) ready for colorization.
/// </summary>
public sealed class DepthEstimationRunner : IDisposable
{
    private InferenceSession? _session;
    public string ActiveBackend { get; private set; } = "Not loaded";
    public bool IsLoaded => _session != null;

    private const int InputSize = 518; // Depth-Anything default
    private const string ModelFileName = "depth-anything-v2-small.onnx";
    private const string DownloadUrl =
        "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx?download=true";

    public async Task InitializeAsync(Action<string>? onStatus = null)
    {
        if (_session != null) return;
        string modelPath = ModelDownloader.GetCachedPath(ModelFileName);
        if (!File.Exists(modelPath))
        {
            onStatus?.Invoke($"Downloading {ModelFileName} from Hugging Face (~99 MB, one-time)...");
            modelPath = await ModelDownloader.EnsureModelAsync(ModelFileName, DownloadUrl,
                onProgress: (got, total, pct) =>
                {
                    if (total > 0)
                        onStatus?.Invoke($"Download: {got / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB ({pct:F0}%)");
                });
        }

        onStatus?.Invoke("Creating inference session...");
        var (session, backend) = await WindowsMlSessionFactory.CreateSessionAsync(modelPath, onStatus, forceCpu: true);
        _session = session;
        ActiveBackend = backend;
        onStatus?.Invoke($"✓ Loaded on {ActiveBackend}");

        foreach (var input in _session.InputMetadata)
            onStatus?.Invoke($"  Input: {input.Key} [{string.Join("×", input.Value.Dimensions)}]");
        foreach (var output in _session.OutputMetadata)
            onStatus?.Invoke($"  Output: {output.Key} [{string.Join("×", output.Value.Dimensions)}]");
    }

    /// <summary>
    /// Runs depth estimation on each provided RGBA frame buffer (any size — will be resized).
    /// Returns one DepthMap per input frame in original input order.
    /// </summary>
    public async Task<List<DepthMap>> EstimateDepthAsync(
        IReadOnlyList<(int frameIndex, byte[] rgbaBytes, int width, int height)> frames,
        Action<int, int>? onProgress = null,
        Action<string>? onDiagnostic = null)
    {
        if (_session == null) throw new InvalidOperationException("Call InitializeAsync first.");
        var results = new List<DepthMap>(frames.Count);

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
                    var depthOut = outs.First();
                    if (depthOut.Value is DenseTensor<float> depth)
                    {
                        results.Add(BuildDepthMap(idx, depth));
                        onDiagnostic?.Invoke($"  Frame {idx + 1}: depth ok ({depth.Dimensions[depth.Dimensions.Length - 2]}×{depth.Dimensions[depth.Dimensions.Length - 1]})");
                    }
                }
                catch (Exception ex)
                {
                    onDiagnostic?.Invoke($"    [diag] frame {i} depth failed: {ex.Message}");
                }
            }
        });

        return results;
    }

    private static DenseTensor<float> PreprocessRgba(byte[] rgba, int srcW, int srcH)
    {
        // Bilinear-ish resize via nearest-neighbor (good enough for depth heatmap)
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };
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

    private static DepthMap BuildDepthMap(int frameIdx, DenseTensor<float> depth)
    {
        // depth shape: [1, H, W] or [1, 1, H, W]
        int h, w; int dimOffset;
        if (depth.Dimensions.Length == 4) { h = depth.Dimensions[2]; w = depth.Dimensions[3]; dimOffset = 4; }
        else { h = depth.Dimensions[1]; w = depth.Dimensions[2]; dimOffset = 3; }

        float min = float.MaxValue, max = float.MinValue;
        var raw = new float[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = dimOffset == 4 ? depth[0, 0, y, x] : depth[0, y, x];
                raw[y * w + x] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        // Normalize to [0,1]
        float range = MathF.Max(1e-6f, max - min);
        var norm = new float[h * w];
        for (int i = 0; i < raw.Length; i++) norm[i] = (raw[i] - min) / range;
        return new DepthMap { FrameIndex = frameIdx, Width = w, Height = h, Normalized = norm, RawMin = min, RawMax = max };
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}

public sealed class DepthMap
{
    public int FrameIndex { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public float[] Normalized { get; init; } = Array.Empty<float>(); // [0,1]
    public float RawMin { get; init; }
    public float RawMax { get; init; }
}
