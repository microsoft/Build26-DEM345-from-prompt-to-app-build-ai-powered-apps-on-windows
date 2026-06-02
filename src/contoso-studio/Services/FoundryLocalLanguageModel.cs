using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AI.Foundry.Local;
using BetalgoChatMessage = Betalgo.Ranul.OpenAI.ObjectModels.RequestModels.ChatMessage;

namespace VideoStudio.Services;

/// <summary>
/// <see cref="ILanguageModel"/> implementation backed by Foundry Local
/// (<see href="https://learn.microsoft.com/azure/ai-foundry/foundry-local/"/>).
///
/// On first use, the bundled SDK downloads + registers execution providers,
/// downloads the requested model, starts a local OpenAI-compatible web service,
/// then we talk to it via the official <c>OpenAI</c> .NET SDK.
///
/// One instance per model alias; instances are cached in
/// <see cref="LanguageModelRegistry"/>.
/// </summary>
public sealed class FoundryLocalLanguageModel : ILanguageModel, IDisposable
{
    public const string IdPrefix = "foundry:";

    private readonly string _modelAlias;
    private readonly object _initLock = new();
    private Task? _initTask;
    private FoundryLocalManager? _manager;
    private IModel? _model;
    private OpenAIChatClient? _chatClient;

    public FoundryLocalLanguageModel(string modelAlias, string? displayName = null)
    {
        _modelAlias = modelAlias ?? throw new ArgumentNullException(nameof(modelAlias));
        DisplayName = displayName ?? $"Foundry · {modelAlias}";
    }

    public string Id => IdPrefix + _modelAlias;
    public string DisplayName { get; }
    public string ActiveBackend { get; private set; } = "Foundry Local (not loaded)";
    public bool IsReady => _chatClient is not null;

    public Task EnsureReadyAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        // De-duplicate concurrent init attempts.
        lock (_initLock)
        {
            if (_initTask is null || _initTask.IsFaulted || _initTask.IsCanceled)
                _initTask = InitializeAsync(onStatus, ct);
            return _initTask;
        }
    }

    private async Task InitializeAsync(Action<string>? onStatus, CancellationToken ct)
    {
        if (_chatClient is not null) return;

        onStatus?.Invoke($"Foundry Local: initializing for {_modelAlias}...");

        _manager = await FoundryBootstrap.EnsureManagerAsync(onStatus);

        // EP registration now happens once inside FoundryBootstrap.

        // Resolve catalog → model. Foundry's online catalog can return HTTP 429
        // ("Received too many requests in a short amount of time") under load; retry
        // with exponential backoff so a single transient burst doesn't fall back to
        // the heuristic path.
        onStatus?.Invoke($"Foundry Local: looking up '{_modelAlias}' in catalog...");
        var (catalog, model) = await GetCatalogModelWithRetryAsync(_manager, _modelAlias, onStatus, ct);

        // Diagnostic: log every variant the SDK is currently exposing so we can see what's
        // actually filtered. This is critical when troubleshooting "why does it always pick
        // CPU?" — if the catalog only contains a -generic-cpu variant, then CUDA/QNN EP
        // didn't register and the catalog is filtering accordingly.
        if (model.Variants is { Count: > 0 } variants)
        {
            onStatus?.Invoke($"Foundry: {variants.Count} variant(s) available for {_modelAlias}:");
            foreach (var v in variants)
            {
                var dt = v.Info?.Runtime?.DeviceType.ToString() ?? "?";
                var vep = v.Info?.Runtime?.ExecutionProvider ?? "?";
                onStatus?.Invoke($"  • {v.Id}  [{dt}/{vep}]");
            }
        }

        // Pick the best variant. Foundry's default selection prefers a locally-cached variant
        // over the highest-priority one — which can lock us onto a half-downloaded CPU build
        // forever. Explicitly walk Variants (priority-ordered) and pick the first GPU/NPU
        // variant; fall back to whatever Foundry picked.
        var selected = SelectBestVariant(model, onStatus);
        if (selected is not null && !ReferenceEquals(selected, model))
        {
            model.SelectVariant(selected);
        }
        var activeVariant = selected ?? model;
        var device = activeVariant.Info?.Runtime?.DeviceType.ToString() ?? "default";
        var ep = activeVariant.Info?.Runtime?.ExecutionProvider ?? "default";

        onStatus?.Invoke($"Foundry Local: downloading {activeVariant.Id} ({device} / {ep})...");
        await activeVariant.DownloadAsync(
            progress => onStatus?.Invoke($"Foundry download: {progress:F0}%"),
            ct);

        onStatus?.Invoke($"Foundry Local: loading {activeVariant.Id}...");
        await activeVariant.LoadAsync(ct);

        // Use the in-process OpenAI ChatClient bound directly to the loaded model.
        // The Foundry "web service" is not included in all SDK builds (StartWebServiceAsync
        // throws "Web service configuration was not provided"), so go straight to the
        // SDK-provided client which talks to the model in-process.
        onStatus?.Invoke("Foundry Local: connecting chat client...");
        _chatClient = await activeVariant.GetChatClientAsync(ct);

        _model = activeVariant;
        ActiveBackend = $"Foundry Local · {_modelAlias} ({device})";
        onStatus?.Invoke($"Foundry Local ready ({_modelAlias} on {device}).");
    }

    /// <summary>
    /// Pick the best variant of a model. <see cref="IModel.Variants"/> is already priority-ordered
    /// by Foundry (highest priority first, per the SDK sample comment) — the catalog reflects what
    /// the SDK considers best for this machine's registered execution providers, so we trust it.
    ///
    /// The only reason we override at all: <c>GetModelAsync</c>'s default selection prefers a
    /// locally-cached variant over the highest-priority one. A stale or partial CPU download will
    /// otherwise pin us to CPU forever. Force-selecting Variants[0] restores the priority order.
    /// Skip the override only if the SDK's default already IS Variants[0] (so we don't log noise),
    /// or if the top variant is CPU (no point overriding to the same tier).
    /// </summary>
    private static IModel? SelectBestVariant(IModel model, Action<string>? onStatus)
    {
        if (model.Variants is not { Count: > 0 } variants) return null;

        var top = variants[0];
        if (ReferenceEquals(top, model) || top.Id == model.Id) return null;

        // If even the top-priority variant is CPU, the SDK already picked CPU; nothing to override.
        if (top.Info?.Runtime?.DeviceType is DeviceType.CPU or DeviceType.Invalid or null
            && model.Info?.Runtime?.DeviceType is DeviceType.CPU)
        {
            return null;
        }

        var fromDesc = $"{model.Id} ({model.Info?.Runtime?.DeviceType}/{model.Info?.Runtime?.ExecutionProvider})";
        var toDesc = $"{top.Id} ({top.Info?.Runtime?.DeviceType}/{top.Info?.Runtime?.ExecutionProvider})";
        onStatus?.Invoke($"Foundry: overriding cached default {fromDesc} → priority pick {toDesc}.");
        return top;
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_chatClient is null) throw new InvalidOperationException("Call EnsureReadyAsync first.");

        var messages = new System.Collections.Generic.List<BetalgoChatMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new BetalgoChatMessage("system", systemPrompt));
        messages.Add(new BetalgoChatMessage("user", userPrompt));

        var sb = new System.Text.StringBuilder();
        await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, ct).WithCancellation(ct))
        {
            var choice = update?.Choices?.FirstOrDefault();
            var delta = choice?.Delta?.Content ?? choice?.Message?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                sb.Append(delta);
                onProgress?.Invoke(StripThinkBlocks(sb.ToString()));
            }
        }

        return StripThinkBlocks(sb.ToString());
    }

    /// <summary>
    /// Reasoning models (Qwen3, DeepSeek-R1, etc.) prefix their answer with a
    /// <c>&lt;think&gt;...&lt;/think&gt;</c> block containing scratchpad reasoning.
    /// Strip those blocks (including any unterminated one mid-stream) so the user
    /// only sees the final answer.
    /// </summary>
    private static string StripThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("<think", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        // Remove complete <think>...</think> blocks (multi-line, case-insensitive).
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, @"<think\b[^>]*>.*?</think>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // If an opening <think> is still pending (streaming, no closing tag yet),
        // hide everything from it onward so the partial scratchpad doesn't leak into the UI.
        var openIdx = cleaned.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (openIdx >= 0)
        {
            cleaned = cleaned[..openIdx];
        }

        return cleaned.TrimStart('\r', '\n', ' ', '\t');
    }

    private static async Task<(ICatalog catalog, IModel model)> GetCatalogModelWithRetryAsync(
        FoundryLocalManager manager, string modelAlias, Action<string>? onStatus, CancellationToken ct)
    {
        const int maxAttempts = 6;
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var catalog = await manager.GetCatalogAsync();
                var model = await catalog.GetModelAsync(modelAlias)
                    ?? throw new InvalidOperationException($"Foundry Local model '{modelAlias}' not found in catalog.");
                return (catalog, model);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientCatalogFailure(ex))
            {
                var hinted = ExtractRetryAfter(ex) ?? delay;
                if (hinted > TimeSpan.FromSeconds(30)) hinted = TimeSpan.FromSeconds(30);
                onStatus?.Invoke($"Foundry catalog throttled (attempt {attempt}/{maxAttempts - 1}). Retrying in {hinted.TotalSeconds:F0}s...");
                try { await Task.Delay(hinted, ct); }
                catch (OperationCanceledException) { throw; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private static bool IsTransientCatalogFailure(Exception ex)
    {
        // Walk the inner-exception chain so we catch HttpRequestException wrapped in
        // FoundryLocalException, plus any aggregate retries the SDK already attempted.
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is System.Net.Http.HttpRequestException http)
            {
                var sc = http.StatusCode;
                if (sc == System.Net.HttpStatusCode.TooManyRequests) return true;
                if (sc is System.Net.HttpStatusCode.RequestTimeout
                    or System.Net.HttpStatusCode.BadGateway
                    or System.Net.HttpStatusCode.ServiceUnavailable
                    or System.Net.HttpStatusCode.GatewayTimeout) return true;
            }
            var msg = cur.Message ?? string.Empty;
            if (msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("429"))
                return true;
        }
        return false;
    }

    private static TimeSpan? ExtractRetryAfter(Exception ex)
    {
        // Foundry surfaces "Retry again after N seconds." in the error message.
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            var msg = cur.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(
                msg, @"[Rr]etry again after\s+(\d+)\s+seconds?");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var sec))
                return TimeSpan.FromSeconds(Math.Max(1, sec));
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (_model is not null) _model.UnloadAsync().Wait(2000);
        }
        catch { /* best effort */ }
        _chatClient = null;
        _model = null;
        _manager = null;
    }
}
