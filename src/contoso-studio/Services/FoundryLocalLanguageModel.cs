using System;
using System.ClientModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AI.Foundry.Local;
using OpenAI;
using OpenAI.Chat;

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
    private ChatClient? _chatClient;

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

        var config = new Configuration
        {
            AppName = "ContosoStudio",
            LogLevel = global::Microsoft.AI.Foundry.Local.LogLevel.Warning,
        };

        await FoundryLocalManager.CreateAsync(config, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        _manager = FoundryLocalManager.Instance;

        // Download / register Execution Providers (one-time, cached).
        var lastEp = string.Empty;
        await _manager.DownloadAndRegisterEpsAsync((epName, percent) =>
        {
            if (epName != lastEp)
            {
                lastEp = epName;
                onStatus?.Invoke($"Foundry EP: {epName} {percent:F0}%");
            }
        });

        // Resolve catalog → model.
        onStatus?.Invoke($"Foundry Local: looking up '{_modelAlias}' in catalog...");
        var catalog = await _manager.GetCatalogAsync();
        var model = await catalog.GetModelAsync(_modelAlias)
            ?? throw new InvalidOperationException($"Foundry Local model '{_modelAlias}' not found in catalog.");

        // Download + load.
        onStatus?.Invoke($"Foundry Local: downloading {_modelAlias}...");
        await model.DownloadAsync(progress => onStatus?.Invoke($"Foundry download: {progress:F0}%"));

        onStatus?.Invoke($"Foundry Local: loading {_modelAlias}...");
        await model.LoadAsync();

        onStatus?.Invoke("Foundry Local: starting web service...");
        await _manager.StartWebServiceAsync();

        var baseUrl = _manager.Urls is { Length: > 0 }
            ? _manager.Urls[0]
            : throw new InvalidOperationException("Foundry web service did not report a URL.");

        var openAi = new OpenAIClient(
            new ApiKeyCredential("notneeded"),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/v1") });

        _model = model;
        _chatClient = openAi.GetChatClient(model.Id);
        ActiveBackend = $"Foundry Local · {_modelAlias}";
        onStatus?.Invoke($"Foundry Local ready ({_modelAlias}).");
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_chatClient is null) throw new InvalidOperationException("Call EnsureReadyAsync first.");

        var messages = new System.Collections.Generic.List<ChatMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new SystemChatMessage(systemPrompt));
        messages.Add(new UserChatMessage(userPrompt));

        var sb = new System.Text.StringBuilder();
        var updates = _chatClient.CompleteChatStreamingAsync(messages, options: null, ct);
        await foreach (var update in updates.WithCancellation(ct))
        {
            if (update.ContentUpdate is { Count: > 0 })
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        sb.Append(part.Text);
                        onProgress?.Invoke(sb.ToString());
                    }
                }
            }
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            if (_model is not null) _model.UnloadAsync().Wait(2000);
            if (_manager is not null) _manager.StopWebServiceAsync().Wait(2000);
        }
        catch { /* best effort */ }
        _chatClient = null;
        _model = null;
        _manager = null;
    }
}
