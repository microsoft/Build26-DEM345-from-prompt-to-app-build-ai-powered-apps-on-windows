using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

public class SmartCutResult
{
    public List<(double Start, double End)> KeepSegments { get; set; } = new();
    public int CutsRemoved { get; set; }
    public double OriginalDurationSec { get; set; }
    public double NewDurationSec { get; set; }
    public double SecondsRemoved => OriginalDurationSec - NewDurationSec;
    public double SavedPercent => OriginalDurationSec > 0 ? (1.0 - NewDurationSec / OriginalDurationSec) : 0;
    public long ElapsedMs { get; set; }
    public string DeviceUsed { get; set; } = "CPU (ffmpeg)";
    public string OutputPath { get; set; } = string.Empty;
}

/// <summary>
/// Trims silent regions out of a video/audio file using ffmpeg's select+aselect filters.
/// </summary>
public static class SmartCutter
{
    /// <summary>
    /// Compute "keep" segments by inverting the silence intervals and dropping any
    /// keep segment shorter than minKeepMs (avoids tiny rapid-fire cuts).
    /// </summary>
    public static List<(double Start, double End)> ComputeKeepSegments(
        IReadOnlyList<SilenceInterval> silence,
        double durationSec,
        int minKeepMs = 200)
    {
        double minKeep = minKeepMs / 1000.0;
        var ordered = silence.OrderBy(i => i.Start).ToList();
        var keep = new List<(double Start, double End)>();
        double cursor = 0;
        foreach (var iv in ordered)
        {
            if (iv.Start > cursor)
                keep.Add((cursor, Math.Min(iv.Start, durationSec)));
            cursor = Math.Max(cursor, iv.End);
        }
        if (cursor < durationSec) keep.Add((cursor, durationSec));

        // Drop too-short keeps and clamp to bounds
        return keep.Where(k => k.End - k.Start >= minKeep && k.Start < durationSec)
                   .Select(k => (Math.Max(0, k.Start), Math.Min(durationSec, k.End)))
                   .ToList();
    }

    public static async Task<SmartCutResult> CutAsync(
        string inputPath,
        IReadOnlyList<SilenceInterval> silence,
        double inputDurationSec,
        string outputPath,
        int minKeepMs = 200,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input not found: {inputPath}");
        if (inputDurationSec <= 0)
            inputDurationSec = await AudioExtractor.ProbeDurationSecondsAsync(inputPath, ct);

        var keep = ComputeKeepSegments(silence, inputDurationSec, minKeepMs);
        onStatus?.Invoke($"Cutting {silence.Count} silent regions → {keep.Count} keep segments");

        if (keep.Count == 0)
            throw new InvalidOperationException("Nothing to keep after cutting silence — try raising the threshold.");

        var sw = Stopwatch.StartNew();

        // Build select expression: +between(t,a1,b1)+between(t,a2,b2)+...
        var sb = new StringBuilder();
        for (int i = 0; i < keep.Count; i++)
        {
            if (i > 0) sb.Append('+');
            sb.Append($"between(t,{keep[i].Start.ToString(System.Globalization.CultureInfo.InvariantCulture):F3},{keep[i].End.ToString(System.Globalization.CultureInfo.InvariantCulture):F3})");
        }
        string between = sb.ToString();

        // Detect if this is video by extension; audio-only files skip the video filter
        string ext = Path.GetExtension(inputPath).ToLowerInvariant();
        bool isAudioOnly = ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".aac";

        string filterArg;
        if (isAudioOnly)
        {
            filterArg = $"-af \"aselect='{between}',asetpts=N/SR/TB\"";
        }
        else
        {
            filterArg =
                $"-vf \"select='{between}',setpts=N/FRAME_RATE/TB\" " +
                $"-af \"aselect='{between}',asetpts=N/SR/TB\"";
        }

        var args =
            $"-nostdin -hide_banner -loglevel error -y -i \"{inputPath}\" {filterArg} " +
            // Re-encode (filter requires it). Reasonable defaults.
            (isAudioOnly
                ? "-c:a aac -b:a 192k"
                : "-c:v libx264 -preset veryfast -crf 22 -c:a aac -b:a 192k") +
            $" \"{outputPath}\"";

        onStatus?.Invoke($"Running ffmpeg ({(isAudioOnly ? "audio re-encode" : "H.264 re-encode")})...");

        var psi = new ProcessStartInfo
        {
            FileName = AudioExtractor.FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit={proc.ExitCode}): {stderr}");

        sw.Stop();

        double newDuration = keep.Sum(k => k.End - k.Start);

        return new SmartCutResult
        {
            KeepSegments = keep,
            CutsRemoved = silence.Count,
            OriginalDurationSec = inputDurationSec,
            NewDurationSec = newDuration,
            ElapsedMs = sw.ElapsedMilliseconds,
            OutputPath = outputPath
        };
    }
}
