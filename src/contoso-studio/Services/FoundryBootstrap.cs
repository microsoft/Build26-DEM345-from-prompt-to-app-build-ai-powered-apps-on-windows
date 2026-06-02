using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AI.Foundry.Local;

namespace VideoStudio.Services;

/// <summary>
/// Process-wide single-shot initialization for <see cref="FoundryLocalManager"/>.
///
/// The SDK throws "FoundryLocalManager has already been created" if
/// <c>CreateAsync</c> is invoked twice. We have two independent callers
/// (live catalog loader in <see cref="LanguageModelRegistry"/> and per-model
/// initialization in <see cref="FoundryLocalLanguageModel"/>), so route both
/// through here.
/// </summary>
internal static class FoundryBootstrap
{
    private static readonly object _lock = new();
    private static Task<FoundryLocalManager>? _initTask;

    /// <summary>Names of EPs that registered successfully on the last bootstrap (or null if not yet bootstrapped).</summary>
    public static IReadOnlyList<string>? RegisteredEps { get; private set; }
    /// <summary>Names of EPs that failed to register on the last bootstrap.</summary>
    public static IReadOnlyList<string>? FailedEps { get; private set; }

    public static Task<FoundryLocalManager> EnsureManagerAsync(Action<string>? onStatus = null)
    {
        lock (_lock)
        {
            return _initTask ??= InitAsync(onStatus);
        }
    }

    private static async Task<FoundryLocalManager> InitAsync(Action<string>? onStatus)
    {
        var config = new Configuration
        {
            AppName = "ContosoStudio",
            LogLevel = global::Microsoft.AI.Foundry.Local.LogLevel.Warning,
            Web = new Configuration.WebService(),
        };

        try
        {
            await FoundryLocalManager.CreateAsync(config, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
        catch (Exception ex) when (ex.Message?.Contains("already been created", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Another path already initialized — reuse the singleton.
        }

        var mgr = FoundryLocalManager.Instance
            ?? throw new InvalidOperationException("FoundryLocalManager.Instance is null after CreateAsync.");

        // Per SDK docs: "To ensure all hardware-accelerated models are listed, call
        // DownloadAndRegisterEpsAsync first." Without this, GetCatalogAsync only sees CPU
        // variants and effects silently fall back to CPU.
        //
        // Surface the result (registered + failed EPs) so the UI shows the truth — silent
        // failure here is what produces "still loading CPU" with no explanation.
        try
        {
            var lastEp = string.Empty;
            var result = await mgr.DownloadAndRegisterEpsAsync((epName, percent) =>
            {
                if (epName != lastEp)
                {
                    lastEp = epName;
                    onStatus?.Invoke($"Foundry EP: {epName} {percent:F0}%");
                }
            });

            RegisteredEps = result.RegisteredEps?.ToArray() ?? Array.Empty<string>();
            FailedEps = result.FailedEps?.Select(f => f.ToString() ?? "").ToArray() ?? Array.Empty<string>();

            if (RegisteredEps.Count > 0)
            {
                onStatus?.Invoke($"Foundry EPs registered: {string.Join(", ", RegisteredEps)}");
            }
            if (FailedEps.Count > 0)
            {
                onStatus?.Invoke($"Foundry EPs FAILED: {string.Join(", ", FailedEps)}");
            }
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Foundry EP registration error: {ex.Message}");
            RegisteredEps = Array.Empty<string>();
            FailedEps = Array.Empty<string>();
        }

        return mgr;
    }
}
