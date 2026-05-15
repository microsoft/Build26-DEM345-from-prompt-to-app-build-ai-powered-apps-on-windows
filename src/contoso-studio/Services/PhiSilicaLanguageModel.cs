using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace VideoStudio.Services;

/// <summary>
/// <see cref="ILanguageModel"/> implementation backed by Windows AI Phi Silica
/// (<c>Microsoft.Windows.AI.Text.LanguageModel</c>). Requires Copilot+ PC.
/// </summary>
public sealed class PhiSilicaLanguageModel : ILanguageModel, IDisposable
{
    public const string BackendId = "phi-silica";

    private global::Microsoft.Windows.AI.Text.LanguageModel? _model;
    private bool _initialized;

    public string Id => BackendId;
    public string DisplayName => "Phi Silica (NPU)";
    public string ActiveBackend { get; private set; } = "NPU (Phi Silica)";
    public bool IsReady => _model is not null;

    public async Task EnsureReadyAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (_initialized && _model is not null) return;

        onStatus?.Invoke("Checking Phi Silica availability...");
        var state = global::Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();

        if (state is global::Microsoft.Windows.AI.AIFeatureReadyState.NotSupportedOnCurrentSystem)
            throw new NotSupportedException("Phi Silica is not supported on this system. Requires a Copilot+ PC.");

        if (state is global::Microsoft.Windows.AI.AIFeatureReadyState.DisabledByUser)
            throw new InvalidOperationException("Phi Silica is disabled in Windows settings.");

        if (state is global::Microsoft.Windows.AI.AIFeatureReadyState.NotReady)
        {
            onStatus?.Invoke("Installing / preparing Phi Silica components (one-time)...");
            var op = await global::Microsoft.Windows.AI.Text.LanguageModel.EnsureReadyAsync().AsTask(ct);
            if (op.Status != global::Microsoft.Windows.AI.AIFeatureReadyResultState.Success)
                throw new InvalidOperationException($"Phi Silica EnsureReady failed: {op.Status} — {op.ExtendedError?.Message}");
        }

        if (global::Microsoft.Windows.AI.Text.LanguageModel.GetReadyState()
            is not global::Microsoft.Windows.AI.AIFeatureReadyState.Ready)
            throw new InvalidOperationException("Phi Silica is not ready after EnsureReadyAsync.");

        onStatus?.Invoke("Loading Phi Silica model...");
        _model = await global::Microsoft.Windows.AI.Text.LanguageModel.CreateAsync().AsTask(ct);
        _initialized = true;
        onStatus?.Invoke("Phi Silica ready.");
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_model is null) throw new InvalidOperationException("Call EnsureReadyAsync first.");

        var ctx = string.IsNullOrEmpty(systemPrompt)
            ? _model.CreateContext()
            : _model.CreateContext(systemPrompt, new global::Microsoft.Windows.AI.ContentSafety.ContentFilterOptions());

        var usable = _model.GetUsablePromptLength(ctx, userPrompt);
        if ((ulong)userPrompt.Length > usable)
        {
            userPrompt = userPrompt.Substring(0, (int)usable);
        }

        var options = new global::Microsoft.Windows.AI.Text.LanguageModelOptions();
        IAsyncOperationWithProgress<global::Microsoft.Windows.AI.Text.LanguageModelResponseResult, string> op =
            _model.GenerateResponseAsync(ctx, userPrompt, options);

        op.Progress = (_, partial) => onProgress?.Invoke(partial);

        var tcs = new TaskCompletionSource<global::Microsoft.Windows.AI.Text.LanguageModelResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Completed = (asyncOp, status) =>
        {
            try
            {
                if (status == AsyncStatus.Completed) tcs.TrySetResult(asyncOp.GetResults());
                else if (status == AsyncStatus.Canceled) tcs.TrySetCanceled();
                else tcs.TrySetException(asyncOp.ErrorCode ?? new Exception("Unknown Phi Silica error"));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };

        using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });

        var result = await tcs.Task.ConfigureAwait(false);

        return result.Status switch
        {
            global::Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Complete => result.Text ?? string.Empty,
            _ => throw new InvalidOperationException($"Phi Silica response status: {result.Status}")
        };
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _initialized = false;
    }
}
