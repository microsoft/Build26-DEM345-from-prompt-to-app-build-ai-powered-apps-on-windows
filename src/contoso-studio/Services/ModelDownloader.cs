using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VideoStudio.Services;

/// <summary>
/// Downloads and caches model files in LocalAppData.
/// Used for first-run setup of large models we don't want to bundle in MSIX.
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        // Big files; let big buffers stream
        AutomaticDecompression = System.Net.DecompressionMethods.None
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    public static string ModelsRoot
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Contoso Studio",
                "models");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>
    /// Returns the cached path for a model file name, regardless of whether it exists yet.
    /// </summary>
    public static string GetCachedPath(string fileName) =>
        Path.Combine(ModelsRoot, fileName);

    /// <summary>
    /// True if the model file exists in the cache (does not verify integrity).
    /// </summary>
    public static bool IsCached(string fileName) =>
        File.Exists(GetCachedPath(fileName));

    /// <summary>
    /// Ensure a model is cached locally. If absent, download it from the URL with progress callbacks.
    /// </summary>
    /// <param name="fileName">The file name to cache (e.g. "whisper-tiny-int8.onnx").</param>
    /// <param name="url">HTTPS URL to download from.</param>
    /// <param name="onProgress">Optional progress callback: (bytesDownloaded, totalBytes, percent).</param>
    /// <param name="onStatus">Optional status text callback.</param>
    public static async Task<string> EnsureModelAsync(
        string fileName,
        string url,
        Action<long, long, double>? onProgress = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var path = GetCachedPath(fileName);
        if (File.Exists(path))
        {
            onStatus?.Invoke($"Model cached: {fileName} ({FormatBytes(new FileInfo(path).Length)})");
            return path;
        }

        var tempPath = path + ".downloading";
        try
        {
            onStatus?.Invoke($"Downloading {fileName}...");
            Log.Info($"Downloading model {fileName} from {url}");

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                int progressTickBytes = totalBytes > 0 ? (int)Math.Max(totalBytes / 100, 81920) : 1024 * 1024;
                long nextTick = progressTickBytes;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    if (downloaded >= nextTick)
                    {
                        var pct = totalBytes > 0 ? (downloaded * 100.0 / totalBytes) : 0;
                        onProgress?.Invoke(downloaded, totalBytes, pct);
                        nextTick = downloaded + progressTickBytes;
                    }
                }
                onProgress?.Invoke(downloaded, totalBytes, 100);
            }

            // Atomic move into place
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);

            onStatus?.Invoke($"Downloaded {fileName} ({FormatBytes(new FileInfo(path).Length)})");
            Log.Info($"Model {fileName} cached at {path}");
            return path;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Log.Error($"Failed to download model {fileName}", ex);
            throw;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
        return $"{bytes} B";
    }
}
