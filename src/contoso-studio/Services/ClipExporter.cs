using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

public sealed class ExportedClip
{
    public int Index { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public string Title { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }

    public double Duration => Math.Max(0, End - Start);
    public string FileName => string.IsNullOrEmpty(OutputPath) ? "" : Path.GetFileName(OutputPath);
    public string FileSizeText => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / 1024.0 / 1024.0:F1} MB",
    };

    public string TimeRangeText => $"{Highlight.FormatTime(Start)} – {Highlight.FormatTime(End)}";
}

public sealed class ClipExportResult
{
    public IReadOnlyList<ExportedClip> Clips { get; init; } = Array.Empty<ExportedClip>();
    public long ElapsedMs { get; init; }
    public string DeviceUsed { get; init; } = "CPU (ffmpeg stream-copy)";
}

/// <summary>
/// Exports each highlight as its own short MP4/audio file using ffmpeg's
/// stream-copy mode (-c copy), so it's fast and lossless. Falls back to
/// re-encoding only when the input has incompatible streams.
/// </summary>
public static class ClipExporter
{
    public static async Task<ClipExportResult> ExportAsync(
        string inputPath,
        IReadOnlyList<Highlight> highlights,
        string workDir,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException("Input media not found", inputPath);

        var sw = Stopwatch.StartNew();
        var clipsDir = Path.Combine(workDir, "clips");
        Directory.CreateDirectory(clipsDir);

        string ext = Path.GetExtension(inputPath).ToLowerInvariant();
        string baseName = Path.GetFileNameWithoutExtension(inputPath);

        var exported = new List<ExportedClip>();
        for (int i = 0; i < highlights.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var h = highlights[i];
            string safeTitle = MakeSafeFileName(h.Title);
            string outPath = Path.Combine(clipsDir, $"{baseName}_clip{i + 1:D2}_{safeTitle}{ext}");

            onStatus?.Invoke($"Exporting clip {i + 1}/{highlights.Count}: {h.Title} ({h.Duration:F1}s)");

            // -ss BEFORE -i = fast keyframe seek; -to is end timestamp.
            // -c copy preserves quality and runs ~realtime/100 (essentially instant).
            string args =
                $"-nostdin -hide_banner -loglevel error -y " +
                $"-ss {h.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-to {h.End.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-i \"{inputPath}\" -c copy -avoid_negative_ts make_zero \"{outPath}\"";

            int exit = await RunFfmpegAsync(args, ct);
            // If stream-copy fails (e.g. start not on keyframe and demuxer is picky), retry with re-encode.
            if (exit != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            {
                onStatus?.Invoke($"  Stream-copy failed for clip {i + 1}; retrying with re-encode...");
                if (File.Exists(outPath)) File.Delete(outPath);
                string reencodeArgs =
                    $"-nostdin -hide_banner -loglevel error -y " +
                    $"-ss {h.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"-to {h.End.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"-i \"{inputPath}\" -c:v libx264 -preset veryfast -crf 22 -c:a aac -b:a 192k \"{outPath}\"";
                exit = await RunFfmpegAsync(reencodeArgs, ct);
                if (exit != 0)
                {
                    onStatus?.Invoke($"  ⚠ Clip {i + 1} failed after re-encode (exit {exit}); skipping.");
                    continue;
                }
            }

            long size = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            exported.Add(new ExportedClip
            {
                Index = i + 1,
                Start = h.Start,
                End = h.End,
                Title = h.Title,
                OutputPath = outPath,
                FileSizeBytes = size,
            });
        }

        sw.Stop();
        return new ClipExportResult
        {
            Clips = exported,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static async Task<int> RunFfmpegAsync(string args, CancellationToken ct)
    {
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
        proc.ErrorDataReceived += (_, _) => { };
        proc.Start();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static string MakeSafeFileName(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "clip";
        var sb = new StringBuilder();
        foreach (char c in title)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            if (sb.Length >= 40) break;
        }
        var s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "clip" : s.ToLowerInvariant();
    }
}
