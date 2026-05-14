using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VideoStudio.Services;

/// <summary>
/// Runs container-based media pipeline stages using the wslc.exe CLI.
/// This is more reliable than direct SDK P/Invoke and handles session/image
/// management automatically.
/// </summary>
public sealed class ContainerRunner : IDisposable
{
    private static readonly string WslcPath = @"C:\Program Files\WSL\wslc.exe";
    private readonly string _workDir;
    private bool _disposed;

    public ContainerRunner()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "VideoStudio", "WslcStorage");
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>
    /// Verify wslc.exe is available.
    /// </summary>
    public Task InitSessionAsync(uint cpus = 4, uint memoryMb = 4096, bool enableGpu = false)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(WslcPath))
                throw new FileNotFoundException($"wslc.exe not found at {WslcPath}. Install WSL Container SDK.");

            // Quick validation — check version
            var (exitCode, stdout, _) = RunWslcSync(["--version"]);
            if (exitCode != 0)
                throw new InvalidOperationException("wslc.exe is not functional");
        });
    }

    /// <summary>
    /// Pull a container image (wslc handles caching automatically).
    /// </summary>
    public Task PullImageAsync(string imageUri, Action<string, double>? onProgress = null)
    {
        return Task.Run(() =>
        {
            onProgress?.Invoke(imageUri, 0);
            var (exitCode, stdout, stderr) = RunWslcSync(["pull", imageUri], output =>
            {
                onProgress?.Invoke(imageUri, 50); // Simple progress indication
            });

            if (exitCode != 0)
                throw new InvalidOperationException($"Failed to pull {imageUri}: {stderr}");

            onProgress?.Invoke(imageUri, 100);
        });
    }

    /// <summary>
    /// Run a command in a container with volume mount and capture output.
    /// wslc handles session, container lifecycle, and image pulls automatically.
    /// </summary>
    public async Task<(int exitCode, string stdout, string stderr)> RunContainerProcessAsync(
        string imageName, string containerName, string[] argv,
        string? windowsMountPath = null, string? containerMountPath = null,
        bool enableGpu = false, Action<string>? onStdOut = null)
    {
        var args = new StringBuilder();
        args.Append("run --rm");
        args.Append($" --name {containerName}");

        if (windowsMountPath != null && containerMountPath != null)
        {
            args.Append($" -v \"{windowsMountPath}:{containerMountPath}\"");
        }

        args.Append($" {imageName}");

        // Append the command and its arguments
        foreach (var arg in argv)
        {
            if (arg.Contains(' '))
                args.Append($" \"{arg}\"");
            else
                args.Append($" {arg}");
        }

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = WslcPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stdoutSb.AppendLine(e.Data);
                onStdOut?.Invoke(e.Data + "\n");
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stderrSb.AppendLine(e.Data);
                // FFmpeg writes progress to stderr — forward it too
                onStdOut?.Invoke(e.Data + "\n");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync().ConfigureAwait(false);

        return (process.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    /// <summary>
    /// Extract audio from video using FFmpeg in a container.
    /// </summary>
    public async Task RunFFmpegExtractAsync(string videoPath, string outputAudioPath, Action<string>? onProgress = null)
    {
        string workDir = Path.GetDirectoryName(videoPath)!;
        string videoFileName = Path.GetFileName(videoPath);
        string audioFileName = Path.GetFileName(outputAudioPath);

        string[] argv = [
            "ffmpeg", "-i", $"/data/{videoFileName}",
            "-vn", "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1",
            $"/data/{audioFileName}", "-y"
        ];

        var (exitCode, stdout, stderr) = await RunContainerProcessAsync(
            "jrottenberg/ffmpeg:latest", "ffmpeg-extract",
            argv, workDir, "/data",
            onStdOut: onProgress);

        if (exitCode != 0)
            throw new InvalidOperationException($"FFmpeg extract failed (exit {exitCode}): {stderr}");
    }

    /// <summary>
    /// Transcribe audio to SRT using whisper.cpp in a container.
    /// </summary>
    public async Task RunWhisperTranscribeAsync(string audioPath, string outputSrtPath, Action<string>? onProgress = null)
    {
        string workDir = Path.GetDirectoryName(audioPath)!;
        string audioFileName = Path.GetFileName(audioPath);

        string[] argv = [
            "/app/main", "-m", "/app/models/ggml-base.en.bin",
            "-f", $"/data/{audioFileName}",
            "--output-srt", "--output-file", "/data/subtitles"
        ];

        var (exitCode, stdout, stderr) = await RunContainerProcessAsync(
            "ayushdh96/whisper-cpp:base.en", "whisper-transcribe",
            argv, workDir, "/data",
            onStdOut: onProgress);

        // whisper outputs to subtitles.srt
        string srtSource = Path.Combine(workDir, "subtitles.srt");
        if (File.Exists(srtSource) && srtSource != outputSrtPath)
        {
            File.Move(srtSource, outputSrtPath, overwrite: true);
        }

        if (exitCode != 0)
            throw new InvalidOperationException($"Whisper transcription failed (exit {exitCode}): {stderr}");
    }

    /// <summary>
    /// Compose final video with subtitles using FFmpeg.
    /// </summary>
    public async Task RunFFmpegComposeAsync(string videoPath, string srtPath, string outputPath, Action<string>? onProgress = null)
    {
        string workDir = Path.GetDirectoryName(videoPath)!;
        string videoFileName = Path.GetFileName(videoPath);
        string srtFileName = Path.GetFileName(srtPath);
        string outputFileName = Path.GetFileName(outputPath);

        // Copy SRT to work dir if not there
        string workSrt = Path.Combine(workDir, srtFileName);
        if (!File.Exists(workSrt) && File.Exists(srtPath))
        {
            File.Copy(srtPath, workSrt);
        }

        string[] argv = [
            "ffmpeg", "-i", $"/data/{videoFileName}",
            "-vf", $"subtitles=/data/{srtFileName}",
            $"/data/{outputFileName}", "-y"
        ];

        var (exitCode, stdout, stderr) = await RunContainerProcessAsync(
            "jrottenberg/ffmpeg:latest", "ffmpeg-compose",
            argv, workDir, "/data",
            onStdOut: onProgress);

        if (exitCode != 0)
            throw new InvalidOperationException($"FFmpeg compose failed (exit {exitCode}): {stderr}");
    }

    private (int exitCode, string stdout, string stderr) RunWslcSync(
        string[] args, Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WslcPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        onOutput?.Invoke(stdout);
        return (process.ExitCode, stdout, stderr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
