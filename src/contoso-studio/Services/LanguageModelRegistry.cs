using System.Collections.Generic;
using System.Linq;

namespace VideoStudio.Services;

/// <summary>
/// Catalog of available <see cref="ILanguageModel"/> backends + a singleton
/// instance cache (instances are heavy; share one per id).
///
/// New backends should be added to <see cref="Catalog"/>. The dropdown shown
/// in each LLM-powered <c>PipelineStepCard</c> binds to <see cref="Catalog"/>
/// directly.
/// </summary>
public static class LanguageModelRegistry
{
    public sealed record Entry(string Id, string DisplayName, string Tagline);

    public static readonly IReadOnlyList<Entry> Catalog = new List<Entry>
    {
        new(PhiSilicaLanguageModel.BackendId, "Phi Silica (NPU)",
            "Built-in Windows AI · Copilot+ PC required"),
        new(FoundryLocalLanguageModel.IdPrefix + "qwen2.5-0.5b",
            "Foundry · Qwen 2.5 0.5B",
            "Tiny, fast — auto-downloads on first use"),
        new(FoundryLocalLanguageModel.IdPrefix + "phi-3.5-mini",
            "Foundry · Phi 3.5 Mini",
            "Better reasoning · ~2 GB download"),
        new(FoundryLocalLanguageModel.IdPrefix + "qwen2.5-7b-instruct",
            "Foundry · Qwen 2.5 7B Instruct",
            "Larger / higher quality · ~5 GB download"),
    };

    private static readonly Dictionary<string, ILanguageModel> _instances = new();
    private static readonly object _lock = new();

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
                _ => throw new System.InvalidOperationException($"Unknown language-model backend id: {id}")
            };
            _instances[id] = created;
            return created;
        }
    }

    private static string DisplayNameFor(string id) =>
        Catalog.FirstOrDefault(c => c.Id == id)?.DisplayName ?? id;
}
