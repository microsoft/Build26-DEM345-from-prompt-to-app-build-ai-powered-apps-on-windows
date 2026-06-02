using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AI.Foundry.Local;
using Microsoft.UI.Dispatching;

namespace VideoStudio.Services;

/// <summary>
/// Catalog of available <see cref="ILanguageModel"/> backends + a singleton
/// instance cache (instances are heavy; share one per id).
///
/// The Phi Silica entry is always present. Foundry Local entries are
/// discovered live from <c>ICatalog.ListModelsAsync()</c> on app startup —
/// the previously hardcoded aliases were not actually valid catalog
/// identifiers, so <c>GetModelAsync</c> would always return null.
/// </summary>
public static class LanguageModelRegistry
{
    public sealed record Entry(string Id, string DisplayName, string Tagline, bool IsEnabled = true);

    private const string FoundryLoadingId = "__foundry_loading__";
    private const string FoundryUnavailableId = "__foundry_unavailable__";

    /// <summary>UI-bound, observable. Always starts with Phi Silica + a transient Foundry placeholder.</summary>
    public static ObservableCollection<Entry> Catalog { get; } = new()
    {
        new Entry(PhiSilicaLanguageModel.BackendId, "Phi Silica (NPU)",
            "Built-in Windows AI · Copilot+ PC required"),
        new Entry(FoundryLoadingId, "Foundry Local · loading models…",
            "Querying live catalog", IsEnabled: false),
    };

    private static readonly Dictionary<string, ILanguageModel> _instances = new();
    private static readonly object _lock = new();
    private static Task? _foundryLoadTask;

    private const string LastUsedSettingsKey = "LastUsedLanguageModelBackendId";
    private static string? _lastUsedBackendId;

    /// <summary>
    /// The most recently picked LLM backend id, used as the default for newly-added steps so
    /// the user doesn't have to re-select their preferred model every time. Persisted to
    /// <see cref="Windows.Storage.ApplicationData.LocalSettings"/> when running packaged, with
    /// an in-memory fallback for unpackaged scenarios.
    /// </summary>
    public static string LastUsedBackendId
    {
        get
        {
            if (_lastUsedBackendId is not null) return _lastUsedBackendId;
            try
            {
                var stored = Windows.Storage.ApplicationData.Current.LocalSettings.Values[LastUsedSettingsKey] as string;
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    _lastUsedBackendId = stored;
                    return stored;
                }
            }
            catch { /* unpackaged → no LocalSettings */ }
            _lastUsedBackendId = PhiSilicaLanguageModel.BackendId;
            return _lastUsedBackendId;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _lastUsedBackendId = value;
            try { Windows.Storage.ApplicationData.Current.LocalSettings.Values[LastUsedSettingsKey] = value; }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Kick off a background task that populates <see cref="Catalog"/> with the
    /// live Foundry Local model list. Safe to call multiple times — only the
    /// first invocation does work. Result is marshaled to <paramref name="ui"/>.
    /// </summary>
    public static Task EnsureFoundryCatalogLoadedAsync(DispatcherQueue ui)
    {
        lock (_lock)
        {
            return _foundryLoadTask ??= Task.Run(() => LoadFoundryCatalogAsync(ui));
        }
    }

    private static async Task LoadFoundryCatalogAsync(DispatcherQueue ui)
    {
        List<Entry>? entries = null;
        string? error = null;

        try
        {
            var manager = await FoundryBootstrap.EnsureManagerAsync();

            ICatalog? catalog = null;
            for (int attempt = 0; attempt < 4 && catalog is null; attempt++)
            {
                try
                {
                    catalog = await manager.GetCatalogAsync();
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < 3)
                {
                    var delay = ExtractRetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    Log.Info($"Foundry catalog transient failure (attempt {attempt + 1}): {ex.Message}. Retrying in {delay.TotalSeconds:F0}s.");
                    await Task.Delay(delay);
                }
            }

            if (catalog is null)
                throw new InvalidOperationException("Foundry catalog unavailable after retries.");

            var models = await catalog.ListModelsAsync();
            entries = new List<Entry>();
            var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var model in models)
            {
                var alias = GetStringProp(model, "Alias") ?? GetStringProp(model, "Id");
                if (string.IsNullOrWhiteSpace(alias)) continue;
                if (!seenAliases.Add(alias)) continue;

                // Filter to chat-capable models (LLM effects only do chat).
                var task = GetStringProp(model, "Task") ?? string.Empty;
                if (!string.IsNullOrEmpty(task) &&
                    !task.Contains("chat", StringComparison.OrdinalIgnoreCase) &&
                    !task.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                    !task.Contains("completion", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var display = GetStringProp(model, "DisplayName")
                              ?? GetStringProp(model, "Name")
                              ?? alias;
                var publisher = GetStringProp(model, "Publisher");
                var sizeStr = FormatSize(model);
                var tagline = string.Join(" · ", new[]
                {
                    publisher,
                    sizeStr,
                    string.IsNullOrEmpty(task) ? null : task,
                }.Where(s => !string.IsNullOrEmpty(s)));

                entries.Add(new Entry(
                    FoundryLocalLanguageModel.IdPrefix + alias,
                    $"Foundry · {display}",
                    string.IsNullOrEmpty(tagline) ? "Auto-downloads on first use" : tagline));
            }

            entries = entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            Log.Info($"Foundry catalog loaded: {entries.Count} chat-capable models.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"Foundry catalog unavailable: {ex.Message}");
        }

        ui.TryEnqueue(() =>
        {
            // Remove transient loading entry.
            for (int i = Catalog.Count - 1; i >= 0; i--)
            {
                if (Catalog[i].Id is FoundryLoadingId or FoundryUnavailableId)
                    Catalog.RemoveAt(i);
            }

            if (entries is { Count: > 0 })
            {
                foreach (var e in entries) Catalog.Add(e);
            }
            else
            {
                Catalog.Add(new Entry(FoundryUnavailableId,
                    "Foundry Local · unavailable",
                    error is null ? "Install Foundry Local to enable" : $"Error: {Trim(error, 80)}",
                    IsEnabled: false));
            }
        });
    }

    /// <summary>Get (or lazily construct + cache) the instance for the given backend id.</summary>
    public static ILanguageModel Resolve(string id)
    {
        if (string.IsNullOrEmpty(id)) id = PhiSilicaLanguageModel.BackendId;

        lock (_lock)
        {
            if (_instances.TryGetValue(id, out var existing)) return existing;

            ILanguageModel created = id switch
            {
                PhiSilicaLanguageModel.BackendId => new PhiSilicaLanguageModel(),
                _ when id.StartsWith(FoundryLocalLanguageModel.IdPrefix) =>
                    new FoundryLocalLanguageModel(
                        id.Substring(FoundryLocalLanguageModel.IdPrefix.Length),
                        DisplayNameFor(id)),
                _ => throw new InvalidOperationException($"Unknown language-model backend id: {id}")
            };
            _instances[id] = created;
            return created;
        }
    }

    private static string DisplayNameFor(string id) =>
        Catalog.FirstOrDefault(c => c.Id == id)?.DisplayName ?? id;

    private static string? GetStringProp(object obj, string name)
    {
        try { return obj.GetType().GetProperty(name)?.GetValue(obj)?.ToString(); }
        catch { return null; }
    }

    private static string? FormatSize(object model)
    {
        try
        {
            var v = model.GetType().GetProperty("Size")?.GetValue(model);
            if (v is null) return null;
            var bytes = Convert.ToInt64(v);
            if (bytes <= 0) return null;
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
            return $"{bytes / 1024.0:F0} KB";
        }
        catch { return null; }
    }

    private static bool IsTransient(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("429") || msg.Contains("503") || msg.Contains("Too many requests", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan? ExtractRetryAfter(Exception ex)
    {
        var m = Regex.Match(ex.Message ?? string.Empty, @"[Rr]etry again after\s+(\d+)\s+seconds?");
        return m.Success && int.TryParse(m.Groups[1].Value, out var s) ? TimeSpan.FromSeconds(Math.Min(s, 30)) : null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
