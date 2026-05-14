using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace VideoStudio.Services;

/// <summary>
/// Shared helpers for creating ONNX Runtime sessions targeting the best available
/// Windows ML EP (NPU → GPU → CPU). Used by every WinML-backed effect runner so
/// each one doesn't have to repeat ~60 lines of EP discovery/registration boilerplate.
/// </summary>
public static class WindowsMlSessionFactory
{
    private static readonly object _envLock = new();
    private static OrtEnv? _ortEnv;

    public static OrtEnv GetOrCreateEnv(Action<string>? onStatus = null)
    {
        lock (_envLock)
        {
            if (_ortEnv != null) return _ortEnv;
            var envOptions = new EnvironmentCreationOptions
            {
                logId = "ContosoStudio",
                logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };
            try { _ortEnv = OrtEnv.CreateInstanceWithOptions(ref envOptions); }
            catch (Exception ex) { onStatus?.Invoke($"OrtEnv creation failed: {ex.Message}"); }
            return _ortEnv ??= OrtEnv.Instance();
        }
    }

    private static bool _epsRegistered;
    private static readonly SemaphoreSlim _epRegLock = new(1, 1);

    /// <summary>
    /// Discovers and registers Windows ML certified EPs (NPU/GPU). Idempotent —
    /// only runs once per process so repeated calls are free.
    /// </summary>
    public static async Task EnsureEpsRegisteredAsync(Action<string>? onStatus = null)
    {
        if (_epsRegistered) return;
        await _epRegLock.WaitAsync();
        try
        {
            if (_epsRegistered) return;
            try
            {
                var catalog = ExecutionProviderCatalog.GetDefault();
                onStatus?.Invoke("Registering certified EPs...");
                await catalog.EnsureAndRegisterCertifiedAsync();
                onStatus?.Invoke("EP registration complete");
            }
            catch (Exception ex)
            {
                onStatus?.Invoke($"⚠ EP registration failed: {ex.Message}");
                Log.Warn($"EP registration failed: {ex.Message}");
            }
            _epsRegistered = true;
        }
        finally { _epRegLock.Release(); }
    }

    /// <summary>
    /// Detects what hardware is actually available (NPU/GPU) and returns a
    /// SessionOptions configured with the best EP selection policy plus a
    /// human-readable backend label.
    /// </summary>
    public static (SessionOptions options, string backend) BuildSessionOptions(Action<string>? onStatus = null)
    {
        bool hasNpu = false, hasGpu = false;
        string? npuEpName = null, gpuEpName = null;
        try
        {
            var env = GetOrCreateEnv(onStatus);
            var epDevices = env.GetEpDevices();
            if (epDevices != null)
            {
                foreach (var dev in epDevices)
                {
                    var hwType = dev.HardwareDevice.Type;
                    if (hwType == OrtHardwareDeviceType.NPU) { hasNpu = true; npuEpName ??= dev.EpName; }
                    else if (hwType == OrtHardwareDeviceType.GPU) { hasGpu = true; gpuEpName ??= dev.EpName; }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"GetEpDevices: {ex.Message}"); }

        var options = new SessionOptions();
        string backend;
        if (hasNpu)
        {
            options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_NPU);
            backend = $"NPU ({npuEpName})";
        }
        else if (hasGpu)
        {
            options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_GPU);
            backend = $"GPU ({gpuEpName})";
        }
        else { backend = "CPU"; }
        onStatus?.Invoke($"Target backend: {backend}");
        return (options, backend);
    }

    /// <summary>
    /// Creates an InferenceSession on the preferred EP, falling back to plain CPU
    /// if the preferred device fails. Returns the session + the actual backend used.
    /// </summary>
    /// <param name="forceCpu">If true, skip NPU/GPU selection and load on plain CPU.
    /// Use for models with patch_embeddings or other ops that QNN sub-graph extraction
    /// breaks at runtime (e.g. ViT/DINOv2/Depth-Anything from transformers.js exports).</param>
    public static async Task<(InferenceSession session, string backend)> CreateSessionAsync(
        string modelPath, Action<string>? onStatus = null, bool forceCpu = false)
    {
        if (forceCpu)
        {
            onStatus?.Invoke("Target backend: CPU (forced — model not QNN-compatible)");
            var cpuOpts = new SessionOptions();
            var session = await Task.Run(() => new InferenceSession(modelPath, cpuOpts));
            return (session, "CPU");
        }
        await EnsureEpsRegisteredAsync(onStatus);
        var (opts, backend) = BuildSessionOptions(onStatus);
        try
        {
            var session = await Task.Run(() => new InferenceSession(modelPath, opts));
            return (session, backend);
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"⚠ {backend} session failed: {ex.Message}. Falling back to CPU.");
            opts.Dispose();
            var cpuOpts = new SessionOptions();
            var session = await Task.Run(() => new InferenceSession(modelPath, cpuOpts));
            return (session, "CPU (fallback)");
        }
    }
}
