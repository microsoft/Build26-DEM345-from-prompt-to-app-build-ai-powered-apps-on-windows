using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Turns a <see cref="TranscriptResult"/> into a list of <see cref="Chapter"/> markers.
/// Primary path: Phi Silica on the NPU (Windows AI). Fallback: deterministic heuristic
/// that buckets segments into roughly even-time chapters with a generated title.
/// </summary>
public sealed class ChapterGenerator
{
    public ChapterGenerator() { }

    public async Task<ChapterResult> GenerateAsync(
        ILanguageModel lm,
        TranscriptResult transcript,
        int targetCount = 6,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (transcript is null) throw new ArgumentNullException(nameof(transcript));
        if (transcript.Segments.Count == 0)
        {
            return new ChapterResult { Chapters = Array.Empty<Chapter>(), DeviceUsed = "n/a (empty transcript)" };
        }

        var sw = Stopwatch.StartNew();

        // Try Phi Silica first.
        try
        {
            await lm.EnsureReadyAsync(onStatus, ct);
            onStatus?.Invoke($"Generating chapters with {lm.DisplayName}...");
            var raw = await CallLmAsync(lm, transcript, targetCount, onStatus, ct);
            onStatus?.Invoke("Parsing chapter response...");
            var parsed = ParseChapters(raw, transcript.AudioDurationSeconds);
            if (parsed.Count > 0)
            {
                sw.Stop();
                return new ChapterResult
                {
                    Chapters = parsed,
                    DeviceUsed = lm.ActiveBackend,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    RawResponse = raw,
                };
            }
            onStatus?.Invoke($"{lm.DisplayName} returned no parseable chapters — falling back to heuristic.");
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"{lm.DisplayName} unavailable ({ex.GetType().Name}: {ex.Message}). Using heuristic fallback.");
        }

        var fallback = HeuristicChapters(transcript, targetCount);
        sw.Stop();
        return new ChapterResult
        {
            Chapters = fallback,
            DeviceUsed = "CPU (heuristic fallback)",
            ElapsedMs = sw.ElapsedMilliseconds,
            UsedFallback = true,
        };
    }

    private async Task<string> CallLmAsync(ILanguageModel lm, TranscriptResult transcript, int targetCount, Action<string>? onStatus, CancellationToken ct)
    {
        // Build a compact transcript view: "[mm:ss] text" lines
        var sb = new StringBuilder();
        foreach (var s in transcript.Segments)
        {
            sb.Append('[').Append(FormatTime(s.Start)).Append("] ").AppendLine(s.Text.Trim());
        }
        var transcriptText = sb.ToString();

        const int maxChars = 8_000; // soft cap to stay within Phi Silica context
        if (transcriptText.Length > maxChars)
            transcriptText = transcriptText.Substring(0, maxChars);

        string system =
            "You are an editorial assistant that segments podcast transcripts into chapter markers. " +
            "Always respond with strict JSON only — no prose, no markdown fences.";

        string user =
            $"Read this transcript and produce up to {targetCount} chapter markers covering the whole episode. " +
            "Each chapter must include a `time` (mm:ss or hh:mm:ss matching a real timestamp from the transcript), a short `title` (3-7 words, Title Case), and a one-sentence `summary`. " +
            "Return JSON in this exact shape: {\"chapters\":[{\"time\":\"00:00\",\"title\":\"...\",\"summary\":\"...\"}]}.\n\n" +
            "Transcript:\n" + transcriptText;

        return await lm.GenerateAsync(system, user, partial => onStatus?.Invoke($"...{partial.Length} chars"), ct);
    }

    private static List<Chapter> ParseChapters(string raw, double audioDuration)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();

        // Strip code fences if Phi added any
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0) cleaned = cleaned.Substring(firstNl + 1);
            int closeFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (closeFence > 0) cleaned = cleaned.Substring(0, closeFence);
            cleaned = cleaned.Trim();
        }

        // Find the first { ... } JSON object in the response
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return new();
        var json = cleaned.Substring(start, end - start + 1);

        List<Chapter> result = new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            foreach (var item in arr.EnumerateArray())
            {
                string time = item.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
                string title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                string summary = item.TryGetProperty("summary", out var su) ? su.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                double start2 = ParseTimestamp(time);
                if (audioDuration > 0 && start2 > audioDuration) continue;
                result.Add(new Chapter { Start = start2, Title = title.Trim(), Summary = summary.Trim() });
            }
        }
        catch (JsonException)
        {
            return new();
        }

        return result.OrderBy(c => c.Start).ToList();
    }

    private static double ParseTimestamp(string ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return 0;
        var m = Regex.Match(ts.Trim(), @"^(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)$");
        if (!m.Success) return 0;
        double h = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? double.Parse(m.Groups[1].Value) : 0;
        double mm = double.Parse(m.Groups[2].Value);
        double ss = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return h * 3600 + mm * 60 + ss;
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    /// <summary>
    /// Deterministic fallback: split the audio into ~equal-time buckets and use the first
    /// few words of each bucket as the chapter title.
    /// </summary>
    private static List<Chapter> HeuristicChapters(TranscriptResult transcript, int targetCount)
    {
        if (transcript.Segments.Count == 0) return new();
        var total = transcript.AudioDurationSeconds > 0
            ? transcript.AudioDurationSeconds
            : transcript.Segments[^1].End;
        if (total <= 0) return new();

        int n = Math.Max(1, Math.Min(targetCount, transcript.Segments.Count));
        double bucket = total / n;

        var chapters = new List<Chapter>();
        for (int i = 0; i < n; i++)
        {
            double bStart = i * bucket;
            // Find first segment whose Start >= bStart
            var seg = transcript.Segments.FirstOrDefault(s => s.Start >= bStart) ?? transcript.Segments[^1];
            string title = TitleFromText(seg.Text);
            chapters.Add(new Chapter
            {
                Start = bStart,
                Title = title,
                Summary = seg.Text.Trim()
            });
        }
        return chapters;
    }

    private static string TitleFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Untitled";
        var words = text.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var pick = words.Take(6);
        var t = string.Join(' ', pick);
        if (t.Length > 60) t = t.Substring(0, 60);
        return char.ToUpper(t[0]) + t.Substring(1);
    }
}
