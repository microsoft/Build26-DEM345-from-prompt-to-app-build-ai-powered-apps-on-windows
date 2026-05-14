using System;
using System.Collections.Generic;

namespace VideoStudio.Models;

/// <summary>
/// One picked highlight clip — a short region of the source media that the
/// generator (Phi Silica or heuristic) considered share-worthy.
/// </summary>
public sealed class Highlight
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    /// <summary>Generator-assigned score 0..1 (higher = better). Heuristic uses sentence-length normalised.</summary>
    public double Score { get; init; }

    public double Duration => Math.Max(0, End - Start);

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static string FormatRange(double start, double end) =>
        $"{FormatTime(start)} – {FormatTime(end)}";
}

public sealed class HighlightResult
{
    public IReadOnlyList<Highlight> Highlights { get; init; } = Array.Empty<Highlight>();
    public string DeviceUsed { get; init; } = "Unknown";
    public long ElapsedMs { get; init; }
    public string RawResponse { get; init; } = string.Empty;
    public bool UsedFallback { get; init; }
}
