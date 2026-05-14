using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

public class CaptionBurnResult
{
    public string OutputPath { get; set; } = string.Empty;
    public int CaptionCount { get; set; }
    public long ElapsedMs { get; set; }
    public string DeviceUsed { get; set; } = "CPU (ffmpeg)";
}

/// <summary>
/// Burns caption text from a transcript into a video file using ffmpeg's
/// <c>subtitles</c> filter (which is libass under the hood).
///
/// Audio-only inputs short-circuit and just pass through.
/// </summary>
public static class CaptionBurner
{
    /// <summary>
    /// Convert a transcript into SRT text.
    /// </summary>
    public static string BuildSrt(IReadOnlyList<TranscriptSegment> segments, int maxCharsPerCue = 80)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;
            string text = seg.Text.Trim();
            if (text.Length > maxCharsPerCue)
            {
                // Wrap roughly at word boundaries so cues stay 1-2 lines.
                text = WrapText(text, maxCharsPerCue);
            }
            sb.AppendLine(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(FormatSrtTime(seg.Start)).Append(" --> ").AppendLine(FormatSrtTime(seg.End));
            sb.AppendLine(text);
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    public static async Task<CaptionBurnResult> BurnAsync(
        string inputPath,
        IReadOnlyList<TranscriptSegment> segments,
        string workDir,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException("Input media not found", inputPath);

        var sw = Stopwatch.StartNew();

        string ext = Path.GetExtension(inputPath).ToLowerInvariant();
        bool isAudioOnly = ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".aac";
        if (isAudioOnly)
        {
            onStatus?.Invoke("Audio-only input — captions cannot be visually burned in. Passing through.");
            sw.Stop();
            return new CaptionBurnResult
            {
                OutputPath = inputPath,
                CaptionCount = segments.Count,
                ElapsedMs = sw.ElapsedMilliseconds,
                DeviceUsed = "CPU (passthrough)",
            };
        }

        // Write SRT next to the output so ffmpeg's subtitles filter can find it.
        string srtPath = Path.Combine(workDir, "captions.srt");
        await File.WriteAllTextAsync(srtPath, BuildSrt(segments), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        onStatus?.Invoke($"Wrote {Path.GetFileName(srtPath)} with {segments.Count} cues");

        string outputPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(inputPath) + "_captions" + ext);

        // ffmpeg's subtitles filter is fussy about Windows paths. Best-practice escape:
        //   1) backslashes → forward slashes
        //   2) escape ':' as '\\:'  (so 'C\:/work/captions.srt' is one filter argument)
        //   3) wrap in single quotes inside the filter
        string filterPath = srtPath.Replace('\\', '/').Replace(":", "\\:");
        const string forceStyle =
            "FontName=Segoe UI,FontSize=22,Bold=1," +
            "PrimaryColour=&H00FFFFFF,OutlineColour=&H80000000,BorderStyle=3," +
            "Outline=2,Shadow=0,MarginV=40,Alignment=2";

        string vf = $"subtitles='{filterPath}':force_style='{forceStyle}'";

        string args =
            $"-nostdin -hide_banner -loglevel error -y -i \"{inputPath}\" " +
            $"-vf \"{vf}\" " +
            "-c:v libx264 -preset veryfast -crf 22 -c:a copy " +
            $"\"{outputPath}\"";

        onStatus?.Invoke("Running ffmpeg subtitles filter (libass)...");

        var psi = new ProcessStartInfo
        {
            FileName = AudioExtractor.FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg subtitles filter failed (exit={proc.ExitCode}): {stderr}");

        sw.Stop();
        return new CaptionBurnResult
        {
            OutputPath = outputPath,
            CaptionCount = segments.Count,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static string FormatSrtTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static string WrapText(string text, int maxLen)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length + 1 + word.Length > maxLen && line.Length > 0)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(line);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line);
        }
        return sb.ToString();
    }
}
