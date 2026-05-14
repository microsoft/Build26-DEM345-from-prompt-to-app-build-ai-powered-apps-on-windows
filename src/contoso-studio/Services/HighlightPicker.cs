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
/// Picks the top-N "social-clip-worthy" moments from a transcript. Primary
/// path: Phi Silica scoring + reasons. Fallback: heuristic that scores
/// segments by length, lexical density, and time-spread.
/// </summary>
public sealed class HighlightPicker
{
    private readonly PhiSilicaRunner _phi;

    public HighlightPicker(PhiSilicaRunner phi)
    {
        _phi = phi;
    }

    public async Task<HighlightResult> PickAsync(
        TranscriptResult transcript,
        int targetCount = 3,
        double targetClipSeconds = 30,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (transcript is null) throw new ArgumentNullException(nameof(transcript));
        targetCount = Math.Clamp(targetCount, 1, 10);
        targetClipSeconds = Math.Clamp(targetClipSeconds, 8, 120);

        var sw = Stopwatch.StartNew();

        try
        {
            await _phi.EnsureReadyAsync(onStatus, ct);
            onStatus?.Invoke($"Asking Phi Silica for {targetCount} highlights...");
            string raw = await CallPhiAsync(transcript, targetCount, targetClipSeconds, onStatus, ct);
            var highlights = ParseHighlights(raw, transcript, targetClipSeconds);
            if (highlights.Count > 0)
            {
                sw.Stop();
                return new HighlightResult
                {
                    Highlights = highlights,
                    DeviceUsed = _phi.ActiveBackend,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    RawResponse = raw,
                };
            }
            onStatus?.Invoke("Phi Silica returned no parseable highlights — using heuristic fallback.");
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Phi Silica unavailable ({ex.GetType().Name}: {ex.Message}). Using heuristic fallback.");
        }

        var fallback = HeuristicHighlights(transcript, targetCount, targetClipSeconds);
        sw.Stop();
        return new HighlightResult
        {
            Highlights = fallback,
            DeviceUsed = "CPU (heuristic fallback)",
            ElapsedMs = sw.ElapsedMilliseconds,
            UsedFallback = true,
        };
    }

    private async Task<string> CallPhiAsync(TranscriptResult transcript, int targetCount, double clipLen, Action<string>? onStatus, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Transcript (timestamped):");
        foreach (var s in transcript.Segments)
            sb.Append('[').Append(TranscriptSegment.FormatTime(s.Start)).Append("] ").AppendLine(s.Text.Trim());

        const int maxChars = 8_000;
        string body = sb.ToString();
        if (body.Length > maxChars) body = body.Substring(0, maxChars);

        string system =
            "You are a podcast clip producer. Identify the most share-worthy moments " +
            "from a transcript: punchy quotes, surprising claims, emotional beats, or strong takeaways. " +
            "Respond ONLY with strict JSON — no preamble, no code fences.";

        string user =
            $"Pick the top {targetCount} highlight clips from this transcript. Each clip should be roughly {clipLen:F0} seconds long " +
            "and make sense on its own. Output JSON in exactly this shape:\n" +
            "{\"highlights\":[{\"start\":\"mm:ss\",\"end\":\"mm:ss\",\"title\":\"<5-7 word teaser>\",\"reason\":\"<one short sentence>\"}]}\n\n" +
            "Source:\n" + body;

        return await _phi.GenerateAsync(system, user, partial => onStatus?.Invoke($"...{partial.Length} chars"), ct);
    }

    private static IReadOnlyList<Highlight> ParseHighlights(string raw, TranscriptResult transcript, double clipLen)
    {
        var list = new List<Highlight>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        string s = raw.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s.Substring(nl + 1);
            int close = s.LastIndexOf("```", StringComparison.Ordinal);
            if (close > 0) s = s.Substring(0, close);
        }

        int firstBrace = s.IndexOf('{');
        int lastBrace = s.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace) return list;
        s = s.Substring(firstBrace, lastBrace - firstBrace + 1);

        try
        {
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("highlights", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;

            double mediaEnd = transcript.Segments.Count > 0 ? transcript.Segments[^1].End : 0;

            foreach (var el in arr.EnumerateArray())
            {
                double start = ParseTime(el.TryGetProperty("start", out var st) ? st.GetString() : null);
                double end = ParseTime(el.TryGetProperty("end", out var en) ? en.GetString() : null);
                string title = el.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                string reason = el.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";

                if (end <= start) end = start + clipLen;
                if (mediaEnd > 0) end = Math.Min(end, mediaEnd);
                if (end <= start) continue;

                list.Add(new Highlight
                {
                    Start = start, End = end,
                    Title = string.IsNullOrWhiteSpace(title) ? $"Clip @ {Highlight.FormatTime(start)}" : title.Trim(),
                    Reason = reason.Trim(),
                    Score = 1.0,
                });
            }
        }
        catch (JsonException) { }

        return list;
    }

    private static double ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        // hh:mm:ss or mm:ss or ss
        var parts = value.Split(':');
        try
        {
            return parts.Length switch
            {
                3 => int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                2 => int.Parse(parts[0]) * 60 + double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                1 => double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                _ => 0,
            };
        }
        catch { return 0; }
    }

    private static IReadOnlyList<Highlight> HeuristicHighlights(TranscriptResult transcript, int targetCount, double clipLen)
    {
        var segments = transcript.Segments;
        if (segments.Count == 0) return Array.Empty<Highlight>();

        // Score each segment by length + word count; weed out filler-only.
        var scored = segments
            .Select((s, i) => new
            {
                Index = i,
                Seg = s,
                Score = ScoreSegment(s.Text),
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Pick targetCount segments while enforcing time-spread (no two within clipLen).
        var picked = new List<Highlight>();
        var usedRanges = new List<(double start, double end)>();

        foreach (var cand in scored)
        {
            if (picked.Count >= targetCount) break;

            double clipStart = Math.Max(0, cand.Seg.Start - 1);
            double clipEnd = clipStart + clipLen;
            if (segments.Count > 0) clipEnd = Math.Min(clipEnd, segments[^1].End);

            // Skip if overlaps an already-picked clip
            if (usedRanges.Any(r => clipStart < r.end && clipEnd > r.start))
                continue;

            string text = cand.Seg.Text.Trim();
            string title = MakeTitle(text);
            picked.Add(new Highlight
            {
                Start = clipStart,
                End = clipEnd,
                Title = title,
                Reason = "Picked by heuristic — long, content-rich segment.",
                Score = cand.Score,
            });
            usedRanges.Add((clipStart, clipEnd));
        }

        return picked.OrderBy(h => h.Start).ToList();
    }

    private static double ScoreSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var words = Regex.Matches(text, @"[A-Za-z][A-Za-z\-']+");
        int wordCount = words.Count;
        // Penalise very short or very long segments; reward novel words (rough proxy).
        var unique = new HashSet<string>(words.Select(m => m.Value.ToLowerInvariant()));
        double novelty = unique.Count / Math.Max(1.0, wordCount);
        double lengthScore = wordCount switch
        {
            < 5 => 0.1,
            < 12 => 0.5,
            < 40 => 1.0,
            < 80 => 0.8,
            _ => 0.5,
        };
        return lengthScore * (0.5 + novelty);
    }

    private static string MakeTitle(string text)
    {
        var words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var teaser = string.Join(' ', words.Take(7));
        if (words.Length > 7) teaser += "…";
        return teaser.TrimEnd('.', ',', ';', ':') + (teaser.EndsWith("…") ? "" : ".");
    }
}
