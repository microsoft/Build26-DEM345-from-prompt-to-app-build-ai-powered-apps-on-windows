using System.Collections.Generic;
using System.Text;

namespace VideoStudio.Models;

/// <summary>
/// One contiguous span of transcribed speech with start/end timestamps in seconds.
/// </summary>
public sealed class TranscriptSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = string.Empty;

    public override string ToString() => $"[{FormatTime(Start)} → {FormatTime(End)}] {Text}";

    public static string FormatTime(double seconds)
    {
        var ts = System.TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.ff")
            : ts.ToString(@"mm\:ss\.ff");
    }
}

/// <summary>
/// Result of a Whisper transcription pass over an audio file.
/// </summary>
public sealed class TranscriptResult
{
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = System.Array.Empty<TranscriptSegment>();
    public string FullText { get; init; } = string.Empty;

    /// <summary>"NPU (QNN)", "GPU", "CPU", etc. — what actually ran.</summary>
    public string DeviceUsed { get; init; } = "Unknown";

    public long ElapsedMs { get; init; }

    /// <summary>Audio length in seconds (for realtime-factor calculation).</summary>
    public double AudioDurationSeconds { get; init; }

    public double RealtimeFactor =>
        AudioDurationSeconds > 0 ? AudioDurationSeconds / (ElapsedMs / 1000.0) : 0;

    /// <summary>
    /// Render as plain text with one segment per line, prefixed by timestamp.
    /// Suitable for the transcript preview tab.
    /// </summary>
    public string ToTimestampedText()
    {
        var sb = new StringBuilder();
        foreach (var seg in Segments)
            sb.AppendLine(seg.ToString());
        return sb.ToString();
    }
}
