using System;
using System.Collections.Generic;

namespace VideoStudio.Models;

/// <summary>
/// One chapter marker — a labeled jump-point into a media file.
/// </summary>
public sealed class Chapter
{
    public double Start { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;

    public override string ToString() => $"[{FormatTime(Start)}] {Title}";

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }
}

/// <summary>
/// Output of a chapter-markers pass on a transcript. Chapters are ordered by Start.
/// </summary>
public sealed class ChapterResult
{
    public IReadOnlyList<Chapter> Chapters { get; init; } = Array.Empty<Chapter>();

    /// <summary>"NPU (Phi Silica)" / "CPU (heuristic fallback)" etc.</summary>
    public string DeviceUsed { get; init; } = "Unknown";

    public long ElapsedMs { get; init; }

    /// <summary>Raw model response, kept for the Logs tab when something looks off.</summary>
    public string RawResponse { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }
}
