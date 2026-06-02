using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace VideoStudio.Services;

/// <summary>
/// Helpers for visualizing per-frame analysis results: colorized depth map overlays
/// and scene-tag captions burned onto frames. Reuses the same Win2D pipeline as
/// BoundingBoxRenderer but for dense pixel output and text overlays.
/// </summary>
public static class FrameOverlayRenderer
{
    /// <summary>
    /// Extracts up to <paramref name="maxFrames"/> evenly-spaced RGBA frames from a video.
    /// Returns the raw pixel data ready to feed into a model. MUST run on UI thread
    /// because Windows.Media.Editing requires STA.
    /// </summary>
    public static async Task<List<(int frameIndex, byte[] rgba, int width, int height)>> ExtractFramesAsync(
        string videoPath, int maxFrames, int targetWidth = 640, int targetHeight = 360,
        Action<int, int>? onProgress = null)
    {
        var frames = new List<(int, byte[], int, int)>();
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            var duration = clip.OriginalDuration;
            double intervalMs = duration.TotalMilliseconds / maxFrames;

            for (int i = 0; i < maxFrames; i++)
            {
                try
                {
                    var ts = TimeSpan.FromMilliseconds(i * intervalMs);
                    var thumb = await composition.GetThumbnailAsync(ts, targetWidth, targetHeight,
                        Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                    var dec = await BitmapDecoder.CreateAsync(thumb);
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = (uint)targetWidth,
                        ScaledHeight = (uint)targetHeight,
                        InterpolationMode = BitmapInterpolationMode.Linear
                    };
                    var pixelData = await dec.GetPixelDataAsync(
                        BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
                        transform, ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);
                    frames.Add((i, pixelData.DetachPixelData(), targetWidth, targetHeight));
                }
                catch (Exception ex)
                {
                    Log.Warn($"FrameOverlay: extract frame {i} failed: {ex.Message}");
                }
                onProgress?.Invoke(i + 1, maxFrames);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"FrameOverlay: open clip failed: {ex.Message}");
        }
        return frames;
    }

    /// <summary>
    /// Builds an MP4 from per-frame depth maps using a turbo-style colormap.
    /// Renders a side-by-side: original frame on the left, depth heatmap on the right.
    /// </summary>
    public static async Task<string?> RenderDepthVideoAsync(
        string sourceVideoPath,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> sourceFrames,
        IReadOnlyList<DepthMap> depthMaps,
        string outputVideoPath,
        Action<string>? onStatus = null)
    {
        if (sourceFrames.Count == 0 || depthMaps.Count == 0) return null;
        string tempDir = Path.Combine(Path.GetTempPath(), "ContosoStudio",
            "depth_frames_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            int srcW = sourceFrames[0].width;
            int srcH = sourceFrames[0].height;
            int outW = srcW * 2;  // side-by-side
            int outH = srcH;
            var savedPaths = new List<string>();

            // Build a depth map index: frameIndex -> DepthMap
            var depthByFrame = new Dictionary<int, DepthMap>();
            foreach (var d in depthMaps) depthByFrame[d.FrameIndex] = d;

            for (int i = 0; i < sourceFrames.Count; i++)
            {
                var (idx, rgba, w, h) = sourceFrames[i];
                onStatus?.Invoke($"Rendering depth frame {i + 1}/{sourceFrames.Count}");

                using var srcBitmap = CanvasBitmap.CreateFromBytes(device, rgba, w, h,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized);

                byte[] depthRgba;
                if (depthByFrame.TryGetValue(idx, out var dm))
                    depthRgba = ColorizeDepthToRgba(dm, w, h);
                else
                    depthRgba = new byte[w * h * 4]; // black

                using var depthBitmap = CanvasBitmap.CreateFromBytes(device, depthRgba, w, h,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized);

                using var rt = new CanvasRenderTarget(device, outW, outH, 96);
                using (var ds = rt.CreateDrawingSession())
                {
                    ds.DrawImage(srcBitmap, new Windows.Foundation.Rect(0, 0, srcW, srcH));
                    ds.DrawImage(depthBitmap, new Windows.Foundation.Rect(srcW, 0, srcW, srcH));
                    // Divider
                    ds.FillRectangle((float)srcW - 1, 0, 2, srcH, Windows.UI.Color.FromArgb(255, 255, 255, 255));
                }

                string outPath = Path.Combine(tempDir, $"depth_{i:D4}.png");
                using (var stream = File.Create(outPath))
                using (var ras = stream.AsRandomAccessStream())
                    await rt.SaveAsync(ras, CanvasBitmapFileFormat.Png);
                savedPaths.Add(outPath);
            }

            return await ComposeFramesIntoMp4Async(sourceVideoPath, savedPaths, outputVideoPath, onStatus);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Burns scene-tag captions onto each source frame in the bottom-left corner.
    /// </summary>
    public static async Task<string?> RenderSceneTagsVideoAsync(
        string sourceVideoPath,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> sourceFrames,
        IReadOnlyList<SceneTag> tags,
        string outputVideoPath,
        Action<string>? onStatus = null)
    {
        if (sourceFrames.Count == 0) return null;
        string tempDir = Path.Combine(Path.GetTempPath(), "ContosoStudio",
            "tag_frames_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            int w = sourceFrames[0].width;
            int h = sourceFrames[0].height;
            var savedPaths = new List<string>();

            // Group tags per frame, ordered by confidence descending
            var tagsByFrame = new Dictionary<int, List<SceneTag>>();
            foreach (var t in tags)
            {
                if (!tagsByFrame.TryGetValue(t.FrameIndex, out var list))
                    tagsByFrame[t.FrameIndex] = list = new List<SceneTag>();
                list.Add(t);
            }

            var tagFormat = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 22,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }
            };

            for (int i = 0; i < sourceFrames.Count; i++)
            {
                var (idx, rgba, fw, fh) = sourceFrames[i];
                onStatus?.Invoke($"Rendering scene-tag frame {i + 1}/{sourceFrames.Count}");

                using var srcBitmap = CanvasBitmap.CreateFromBytes(device, rgba, fw, fh,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized);

                using var rt = new CanvasRenderTarget(device, fw, fh, 96);
                using (var ds = rt.CreateDrawingSession())
                {
                    ds.DrawImage(srcBitmap);
                    if (tagsByFrame.TryGetValue(idx, out var frameTags))
                    {
                        float y = fh - 16;
                        for (int j = frameTags.Count - 1; j >= 0; j--)
                        {
                            var t = frameTags[j];
                            string text = $"{t.Label}  {t.Confidence:P0}";
                            using var layout = new CanvasTextLayout(device, text, tagFormat,
                                float.PositiveInfinity, float.PositiveInfinity);
                            float tw = (float)layout.LayoutBounds.Width + 16;
                            float th = (float)layout.LayoutBounds.Height + 6;
                            y -= (th + 4);
                            ds.FillRectangle(16, y, tw, th, Windows.UI.Color.FromArgb(200, 14, 138, 126));
                            ds.DrawTextLayout(layout, 24, y + 1, Windows.UI.Color.FromArgb(255, 255, 255, 255));
                        }
                    }
                }

                string outPath = Path.Combine(tempDir, $"tag_{i:D4}.png");
                using (var stream = File.Create(outPath))
                using (var ras = stream.AsRandomAccessStream())
                    await rt.SaveAsync(ras, CanvasBitmapFileFormat.Png);
                savedPaths.Add(outPath);
            }

            return await ComposeFramesIntoMp4Async(sourceVideoPath, savedPaths, outputVideoPath, onStatus);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task<string?> ComposeFramesIntoMp4Async(
        string sourceVideoPath, IReadOnlyList<string> framePaths, string outputVideoPath, Action<string>? onStatus)
    {
        if (framePaths.Count == 0) return null;
        try
        {
            var srcFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(sourceVideoPath);
            var srcClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(srcFile);
            var totalDuration = srcClip.OriginalDuration;
            var frameDuration = TimeSpan.FromMilliseconds(totalDuration.TotalMilliseconds / framePaths.Count);

            onStatus?.Invoke("Building output video...");
            var composition = new Windows.Media.Editing.MediaComposition();
            foreach (var p in framePaths)
            {
                var f = await Windows.Storage.StorageFile.GetFileFromPathAsync(p);
                var clip = await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(f, frameDuration);
                composition.Clips.Add(clip);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputVideoPath)!);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputVideoPath)!);
            var outFile = await folder.CreateFileAsync(Path.GetFileName(outputVideoPath),
                Windows.Storage.CreationCollisionOption.ReplaceExisting);
            var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
                Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);

            onStatus?.Invoke("Encoding MP4...");
            var renderOp = composition.RenderToFileAsync(outFile,
                Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
            renderOp.Progress = (info, progress) => onStatus?.Invoke($"Encoding: {progress:F0}%");
            var result = await renderOp;
            if (result != Windows.Media.Transcoding.TranscodeFailureReason.None)
            {
                onStatus?.Invoke($"Encoding failed: {result}");
                return null;
            }
            onStatus?.Invoke($"Output: {outFile.Path}");
            return outFile.Path;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Compose failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds an MP4 from per-frame CLIP similarity scores. Every sampled frame appears
    /// in the output; matches above <paramref name="matchThreshold"/> get a labelled border
    /// + score badge to make hits jump out. Top matches additionally get a "★ MATCH" callout.
    /// </summary>
    public static async Task<string?> RenderFindFramesVideoAsync(
        string sourceVideoPath,
        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> sourceFrames,
        IReadOnlyList<(int frameIndex, float score)> scoresIn,
        IReadOnlyCollection<int> topMatchFrameIndices,
        string prompt,
        float matchThreshold,
        string outputVideoPath,
        Action<string>? onStatus = null)
    {
        if (sourceFrames.Count == 0) return null;
        string tempDir = Path.Combine(Path.GetTempPath(), "ContosoStudio",
            "find_frames_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var savedPaths = new List<string>();
            var scoreByFrame = new Dictionary<int, float>();
            foreach (var s in scoresIn) scoreByFrame[s.frameIndex] = s.score;
            var topSet = new HashSet<int>(topMatchFrameIndices);

            var promptFormat = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 18,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
            };
            var scoreFormat = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 22,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }
            };

            for (int i = 0; i < sourceFrames.Count; i++)
            {
                var (idx, rgba, fw, fh) = sourceFrames[i];
                onStatus?.Invoke($"Rendering find-frames frame {i + 1}/{sourceFrames.Count}");

                using var srcBitmap = CanvasBitmap.CreateFromBytes(device, rgba, fw, fh,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized);
                using var rt = new CanvasRenderTarget(device, fw, fh, 96);
                using (var ds = rt.CreateDrawingSession())
                {
                    ds.DrawImage(srcBitmap);

                    float score = scoreByFrame.TryGetValue(idx, out var s) ? s : 0f;
                    bool isMatch = score >= matchThreshold;
                    bool isTop = topSet.Contains(idx);

                    // Prompt strip along the top
                    var stripColor = Windows.UI.Color.FromArgb(180, 14, 138, 126);
                    ds.FillRectangle(0, 0, fw, 32, stripColor);
                    using (var ptLayout = new CanvasTextLayout(device, $"🔍  {prompt}", promptFormat,
                        fw - 24, 32))
                    {
                        ds.DrawTextLayout(ptLayout, 12, 4, Windows.UI.Color.FromArgb(255, 255, 255, 255));
                    }

                    if (isMatch || isTop)
                    {
                        var borderColor = isTop
                            ? Windows.UI.Color.FromArgb(255, 232, 125, 47)   // container orange — top hit
                            : Windows.UI.Color.FromArgb(255, 14, 138, 126);  // teal — above threshold
                        // 6-px border
                        ds.DrawRectangle(3, 3, fw - 6, fh - 6, borderColor, 6);
                    }

                    // Score badge bottom-right
                    string scoreText = isTop ? $"★ {score:F2}" : $"{score:F2}";
                    using (var sLayout = new CanvasTextLayout(device, scoreText, scoreFormat,
                        float.PositiveInfinity, float.PositiveInfinity))
                    {
                        float bw = (float)sLayout.LayoutBounds.Width + 18;
                        float bh = (float)sLayout.LayoutBounds.Height + 8;
                        float bx = fw - bw - 14;
                        float by = fh - bh - 14;
                        var badge = isTop
                            ? Windows.UI.Color.FromArgb(220, 232, 125, 47)
                            : isMatch
                                ? Windows.UI.Color.FromArgb(220, 14, 138, 126)
                                : Windows.UI.Color.FromArgb(160, 0, 0, 0);
                        ds.FillRectangle(bx, by, bw, bh, badge);
                        ds.DrawTextLayout(sLayout, bx + 9, by + 3,
                            Windows.UI.Color.FromArgb(255, 255, 255, 255));
                    }
                }

                string outPath = Path.Combine(tempDir, $"find_{i:D4}.png");
                using (var stream = File.Create(outPath))
                using (var ras = stream.AsRandomAccessStream())
                    await rt.SaveAsync(ras, CanvasBitmapFileFormat.Png);
                savedPaths.Add(outPath);
            }

            return await ComposeFramesIntoMp4Async(sourceVideoPath, savedPaths, outputVideoPath, onStatus);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Maps a normalized depth map [0,1] to RGBA bytes using a turbo-like colormap
    /// (cool blue near, warm red far). Resizes to target W×H by nearest-neighbor.
    /// </summary>
    private static byte[] ColorizeDepthToRgba(DepthMap dm, int targetW, int targetH)
    {
        var output = new byte[targetW * targetH * 4];
        for (int y = 0; y < targetH; y++)
        {
            int sy = (int)((long)y * dm.Height / targetH);
            for (int x = 0; x < targetW; x++)
            {
                int sx = (int)((long)x * dm.Width / targetW);
                float v = dm.Normalized[sy * dm.Width + sx];
                var (r, g, b) = TurboColor(v);
                int p = (y * targetW + x) * 4;
                output[p] = r;
                output[p + 1] = g;
                output[p + 2] = b;
                output[p + 3] = 255;
            }
        }
        return output;
    }

    /// <summary>
    /// Approximation of Google's "turbo" colormap. Input t in [0,1].
    /// </summary>
    private static (byte r, byte g, byte b) TurboColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        // Polynomial approximation
        float r = 34.61f + t * (1172.33f + t * (-10793.56f + t * (33300.12f + t * (-38394.49f + t * 14825.05f))));
        float g = 23.31f + t * (557.33f + t * (1225.33f + t * (-3574.96f + t * (1073.77f + t * 707.56f))));
        float b = 27.2f + t * (3211.1f + t * (-15327.97f + t * (27814f + t * (-22569.18f + t * 6838.66f))));
        return (
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255)
        );
    }
}
