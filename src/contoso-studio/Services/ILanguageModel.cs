using System;
using System.Threading;
using System.Threading.Tasks;

namespace VideoStudio.Services;

/// <summary>
/// Pluggable on-device language model backend. Implementations include
/// <see cref="PhiSilicaLanguageModel"/> (Windows AI / NPU) and
/// <see cref="FoundryLocalLanguageModel"/> (Foundry Local web service).
///
/// Used by Phi Silica-flavored effects: Chapter Markers, Show Notes, Highlights.
/// </summary>
public interface ILanguageModel
{
    /// <summary>Stable identifier (e.g. "phi-silica", "foundry:qwen2.5-0.5b").</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the per-step backend dropdown.</summary>
    string DisplayName { get; }

    /// <summary>Last-known device label (e.g. "NPU (Phi Silica)", "Foundry · qwen2.5-0.5b").</summary>
    string ActiveBackend { get; }

    /// <summary>True after <see cref="EnsureReadyAsync"/> has succeeded at least once.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Lazy initialize the backend — install components / download model / start
    /// services as needed. Safe to call repeatedly. Throws on hard failure
    /// (caller should fall back gracefully).
    /// </summary>
    Task EnsureReadyAsync(Action<string>? onStatus = null, CancellationToken ct = default);

    /// <summary>
    /// Generate a single response. <paramref name="onProgress"/> receives streaming
    /// partial text when the backend supports it.
    /// </summary>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}
