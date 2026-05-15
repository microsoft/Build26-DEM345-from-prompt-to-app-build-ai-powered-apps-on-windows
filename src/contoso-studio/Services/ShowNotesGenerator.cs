using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Turns a <see cref="TranscriptResult"/> (and optionally <see cref="ChapterResult"/>)
/// into markdown "show notes" suitable for a podcast description: short summary,
/// key takeaways, and topic tags.
///
/// Primary path: Phi Silica on the NPU. Fallback: deterministic markdown built
/// from the transcript / chapter list so the demo always produces something.
/// </summary>
public sealed class ShowNotesGenerator
{
    public ShowNotesGenerator() { }

    public async Task<ShowNotesResult> GenerateAsync(
        ILanguageModel lm,
        TranscriptResult transcript,
        ChapterResult? chapters = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (transcript is null) throw new ArgumentNullException(nameof(transcript));

        var sw = Stopwatch.StartNew();

        // Try Phi Silica first.
        try
        {
            await lm.EnsureReadyAsync(onStatus, ct);
            onStatus?.Invoke($"Generating show notes with {lm.DisplayName}...");
            string raw = await CallLmAsync(lm, transcript, chapters, onStatus, ct);
            string md = CleanMarkdown(raw);
            var blocks = ParseMarkdown(md);
            if (blocks.Count > 0)
            {
                sw.Stop();
                return new ShowNotesResult
                {
                    Markdown = md,
                    Blocks = blocks,
                    DeviceUsed = lm.ActiveBackend,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    RawResponse = raw,
                };
            }
            onStatus?.Invoke("Phi Silica returned empty notes — falling back to heuristic.");
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Phi Silica unavailable ({ex.GetType().Name}: {ex.Message}). Using heuristic fallback.");
        }

        // Heuristic fallback.
        string fallbackMd = BuildHeuristicMarkdown(transcript, chapters);
        var fallbackBlocks = ParseMarkdown(fallbackMd);
        sw.Stop();
        return new ShowNotesResult
        {
            Markdown = fallbackMd,
            Blocks = fallbackBlocks,
            DeviceUsed = "CPU (heuristic fallback)",
            ElapsedMs = sw.ElapsedMilliseconds,
            UsedFallback = true,
        };
    }

    private async Task<string> CallLmAsync(ILanguageModel lm, TranscriptResult transcript, ChapterResult? chapters, Action<string>? onStatus, CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (chapters is { Chapters.Count: > 0 })
        {
            sb.AppendLine("Chapter outline:");
            foreach (var c in chapters.Chapters)
                sb.Append("- [").Append(Chapter.FormatTime(c.Start)).Append("] ").AppendLine(c.Title);
            sb.AppendLine();
        }

        sb.AppendLine("Transcript:");
        foreach (var s in transcript.Segments)
            sb.Append('[').Append(TranscriptSegment.FormatTime(s.Start)).Append("] ").AppendLine(s.Text.Trim());

        const int maxChars = 8_000;
        string body = sb.ToString();
        if (body.Length > maxChars) body = body.Substring(0, maxChars);

        string system =
            "You are an editorial assistant that writes podcast / video show notes. " +
            "Respond in concise GitHub-flavoured markdown only — no code fences, no preamble.";

        string user =
            "Write show notes for this episode. Use exactly this structure:\n\n" +
            "## Summary\n" +
            "<2-3 sentences capturing what the episode is about>\n\n" +
            "## Key Takeaways\n" +
            "- <bullet 1>\n- <bullet 2>\n- <bullet 3>\n- <bullet 4 — optional>\n\n" +
            "## Topics\n" +
            "<comma-separated list of 4-7 short topic tags>\n\n" +
            "Source material:\n" + body;

        return await lm.GenerateAsync(system, user, partial => onStatus?.Invoke($"...{partial.Length} chars"), ct);
    }

    private static string CleanMarkdown(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s.Substring(nl + 1);
            int close = s.LastIndexOf("```", StringComparison.Ordinal);
            if (close > 0) s = s.Substring(0, close);
            s = s.Trim();
        }
        return s;
    }

    /// <summary>
    /// Tiny markdown subset parser: headings (#, ##, ###), bullets (- or *), paragraphs.
    /// Plenty for show-notes rendering without a full md engine.
    /// </summary>
    public static IReadOnlyList<ShowNotesBlock> ParseMarkdown(string md)
    {
        var blocks = new List<ShowNotesBlock>();
        if (string.IsNullOrWhiteSpace(md)) return blocks;

        var paragraphBuf = new StringBuilder();
        void FlushParagraph()
        {
            if (paragraphBuf.Length > 0)
            {
                blocks.Add(new ShowNotesBlock { Kind = ShowNotesBlockKind.Paragraph, Text = paragraphBuf.ToString().Trim() });
                paragraphBuf.Clear();
            }
        }

        foreach (var rawLine in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("### "))
            {
                FlushParagraph();
                blocks.Add(new ShowNotesBlock { Kind = ShowNotesBlockKind.Heading, Level = 3, Text = line.Substring(4).Trim() });
                continue;
            }
            if (line.StartsWith("## "))
            {
                FlushParagraph();
                blocks.Add(new ShowNotesBlock { Kind = ShowNotesBlockKind.Heading, Level = 2, Text = line.Substring(3).Trim() });
                continue;
            }
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                blocks.Add(new ShowNotesBlock { Kind = ShowNotesBlockKind.Heading, Level = 1, Text = line.Substring(2).Trim() });
                continue;
            }

            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                FlushParagraph();
                blocks.Add(new ShowNotesBlock { Kind = ShowNotesBlockKind.Bullet, Text = StripInlineFormatting(trimmed.Substring(2).Trim()) });
                continue;
            }

            if (paragraphBuf.Length > 0) paragraphBuf.Append(' ');
            paragraphBuf.Append(StripInlineFormatting(line));
        }

        FlushParagraph();
        return blocks;
    }

    private static string StripInlineFormatting(string s)
    {
        // Strip a small amount of inline md noise so the rendered text reads cleanly.
        return s.Replace("**", "").Replace("__", "");
    }

    private static string BuildHeuristicMarkdown(TranscriptResult transcript, ChapterResult? chapters)
    {
        var sb = new StringBuilder();

        // Summary: first 2-3 sentences of the transcript.
        var sentences = SplitSentences(transcript.FullText).Take(3).ToList();
        sb.AppendLine("## Summary");
        sb.AppendLine(sentences.Count > 0
            ? string.Join(' ', sentences)
            : "Episode summary not available — Phi Silica was unreachable and the transcript was empty.");
        sb.AppendLine();

        // Key Takeaways: chapter titles when available, otherwise pick longer sentences.
        sb.AppendLine("## Key Takeaways");
        IEnumerable<string> bullets;
        if (chapters is { Chapters.Count: > 0 })
            bullets = chapters.Chapters.Select(c => string.IsNullOrWhiteSpace(c.Summary) ? c.Title : c.Summary).Take(5);
        else
            bullets = SplitSentences(transcript.FullText).OrderByDescending(s => s.Length).Take(4);

        foreach (var b in bullets)
            sb.Append("- ").AppendLine(b.Trim().TrimEnd('.') + ".");
        sb.AppendLine();

        // Topics: top non-trivial words by frequency.
        sb.AppendLine("## Topics");
        sb.AppendLine(string.Join(", ", ExtractTopics(transcript.FullText, 6)));

        return sb.ToString();
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
        return parts.Select(p => p.Trim()).Where(p => p.Length > 4);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","of","in","on","for","with","to","from","by","at","as","is","are","was","were",
        "be","been","being","this","that","these","those","it","its","i","you","we","they","he","she","them","us","our",
        "your","my","me","him","her","do","does","did","done","have","has","had","having","will","would","could","should",
        "can","may","might","just","so","not","no","yes","there","here","then","than","what","which","who","whom","whose",
        "when","where","why","how","about","into","over","under","up","down","out","off","more","most","some","any","all"
    };

    private static IEnumerable<string> ExtractTopics(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return new[] { "General" };
        var words = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z][A-Za-z\-']{2,}")
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w))
            .GroupBy(w => w.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Select(g => Capitalize(g.Key))
            .Take(max)
            .ToList();
        return words.Count > 0 ? words : new List<string> { "General" };
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
