using System;
using System.Collections.Generic;

namespace VideoStudio.Models;

/// <summary>
/// One parsed "block" of show-notes content. Used by the Notes tab to render
/// markdown without pulling in a full markdown engine.
/// </summary>
public sealed class ShowNotesBlock
{
    public ShowNotesBlockKind Kind { get; init; }

    /// <summary>Heading level (1..3) when Kind == Heading. Otherwise 0.</summary>
    public int Level { get; init; }

    public string Text { get; init; } = string.Empty;
}

public enum ShowNotesBlockKind
{
    Paragraph,
    Heading,
    Bullet,
}

/// <summary>
/// Output of a show-notes generation pass. Holds both the raw markdown (for
/// copy-paste / file artifact) and a parsed block list (for in-app rendering).
/// </summary>
public sealed class ShowNotesResult
{
    public string Markdown { get; init; } = string.Empty;
    public IReadOnlyList<ShowNotesBlock> Blocks { get; init; } = Array.Empty<ShowNotesBlock>();

    /// <summary>"NPU (Phi Silica)" / "CPU (heuristic fallback)" etc.</summary>
    public string DeviceUsed { get; init; } = "Unknown";

    public long ElapsedMs { get; init; }

    public bool UsedFallback { get; init; }

    public string RawResponse { get; init; } = string.Empty;

    public bool HasContent => Blocks.Count > 0 || !string.IsNullOrWhiteSpace(Markdown);
}
