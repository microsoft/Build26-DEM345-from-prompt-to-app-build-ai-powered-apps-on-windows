using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoStudio.Services;

/// <summary>
/// Extracts mono 16 kHz PCM audio from any media file via the bundled ffmpeg.exe.
/// This is the format Whisper expects.
/// </summary>
public static class AudioExtractor
{
    /// <summary>
    /// Path to the ffmpeg binary that ships in the app's tools/ folder.
    /// </summary>
    public static string FfmpegPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var path = Path.Combine(baseDir, "tools", "ffmpeg.exe");
            if (!File.Exists(path))
                throw new FileNotFoundException($"ffmpeg.exe not found at expected location: {path}");
            return path;
        }
    }

    /// <summary>
    /// Extracts audio from any media file as 16-bit mono PCM at 16 kHz.
    /// Returns the raw PCM bytes (little-endian signed 16-bit samples).
    /// </summary>
    public static async Task<byte[]> ExtractPcm16Mono16kAsync(
        string inputPath,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input media not found: {inputPath}");

        onStatus?.Invoke($"Extracting audio from {Path.GetFileName(inputPath)}...");
        Log.Info($"AudioExtractor: input={inputPath}");

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            // -nostdin: don't try to read keyboard
            // -hide_banner -loglevel error: keep stderr clean
            // -i input
            // -vn: no video
            // -ac 1: mono
            // -ar 16000: 16 kHz
            // -f s16le: raw little-endian 16-bit PCM
            // pipe:1: write to stdout
            Arguments = $"-nostdin -hide_banner -loglevel error -i \"{inputPath}\" -vn -ac 1 -ar 16000 -f s16le pipe:1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.exe");

        // Read stdout (PCM bytes) and stderr concurrently to avoid pipe deadlock
        using var ms = new MemoryStream();
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);

        await Task.WhenAll(copyTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await stderrTask;
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {err}");
        }

        var bytes = ms.ToArray();
        var seconds = bytes.Length / 2.0 / 16000.0;
        onStatus?.Invoke($"Extracted {bytes.Length / 1024} KB of PCM ({seconds:F1}s of audio)");
        Log.Info($"AudioExtractor: extracted {bytes.Length} bytes ({seconds:F1}s)");
        return bytes;
    }

    /// <summary>
    /// Extracts a sub-range (in seconds) of audio as 16-bit mono PCM at 16 kHz.
    /// Used for chunked transcription of long-form audio.
    /// </summary>
    public static async Task<byte[]> ExtractPcm16Mono16kRangeAsync(
        string inputPath,
        double startSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input media not found: {inputPath}");

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            // -ss before -i is the fast seek (within keyframes) — fine for audio
            Arguments = $"-nostdin -hide_banner -loglevel error -ss {startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} -t {durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{inputPath}\" -vn -ac 1 -ar 16000 -f s16le pipe:1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.exe");

        using var ms = new MemoryStream();
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await Task.WhenAll(copyTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await stderrTask;
            throw new InvalidOperationException($"ffmpeg range extract failed (exit {proc.ExitCode}): {err}");
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Probe the duration (in seconds) of a media file's audio track via ffmpeg.
    /// </summary>
    public static async Task<double> ProbeDurationSecondsAsync(string inputPath, CancellationToken ct = default)
    {
        // Use ffmpeg with -i and parse stderr — no ffprobe needed
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = $"-nostdin -hide_banner -i \"{inputPath}\" -f null -",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.exe");
        var err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        // Look for "Duration: HH:MM:SS.ms," in stderr
        var match = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!match.Success) return 0;
        var h = int.Parse(match.Groups[1].Value);
        var m = int.Parse(match.Groups[2].Value);
        var s = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return h * 3600 + m * 60 + s;
    }
}
