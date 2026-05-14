using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using VideoStudio.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace VideoStudio.Services;

/// <summary>
/// Renders bounding box overlays + labels onto video frames using Win2D.
/// </summary>
public static class BoundingBoxRenderer
{
    private static readonly Windows.UI.Color[] BoxColors =
    [
        Windows.UI.Color.FromArgb(255, 0, 220, 110),    // green
        Windows.UI.Color.FromArgb(255, 255, 140, 0),    // orange
        Windows.UI.Color.FromArgb(255, 0, 180, 255),    // cyan
        Windows.UI.Color.FromArgb(255, 255, 60, 60),    // red
        Windows.UI.Color.FromArgb(255, 200, 0, 255),    // purple
        Windows.UI.Color.FromArgb(255, 240, 220, 0),    // yellow
        Windows.UI.Color.FromArgb(255, 255, 90, 200),   // pink
    ];

    /// <summary>
    /// Picks a stable color for a label so the same class is always the same color.
    /// </summary>
    private static Windows.UI.Color ColorForLabel(string label)
        => BoxColors[Math.Abs(label.GetHashCode()) % BoxColors.Length];

    /// <summary>
    /// Extracts frames from a video, draws annotated boxes + labels, saves PNG files.
    /// Returns list of saved frame paths (in order).
    /// </summary>
    public static async Task<List<string>> RenderAnnotatedFramesAsync(
        string videoPath,
        List<DetectionResult> detections,
        string outputDir,
        int maxFrames = 30,
        int frameWidth = 1280,
        int frameHeight = 720,
        Action<int, int>? onProgress = null)
    {
        Directory.CreateDirectory(outputDir);
        var savedPaths = new List<string>();

        Windows.Media.Editing.MediaComposition? composition = null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            var duration = clip.OriginalDuration;
            double intervalMs = duration.TotalMilliseconds / maxFrames;

            var device = CanvasDevice.GetSharedDevice();
            var labelFormat = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = 16,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
            };

            for (int i = 0; i < maxFrames; i++)
            {
                onProgress?.Invoke(i + 1, maxFrames);
                try
                {
                    var timeOffset = TimeSpan.FromMilliseconds(i * intervalMs);
                    var thumbnail = await composition.GetThumbnailAsync(
                        timeOffset, frameWidth, frameHeight,
                        Windows.Media.Editing.VideoFramePrecision.NearestFrame);

                    // Decode the thumbnail (a JPEG-ish stream) into BGRA8 pixels
                    var decoder = await BitmapDecoder.CreateAsync(thumbnail);
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = (uint)frameWidth,
                        ScaledHeight = (uint)frameHeight,
                    };
                    var pixelData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        transform, ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);
                    byte[] frameBytes = pixelData.DetachPixelData();

                    // Build a CanvasBitmap from the pixel buffer, then composite onto a render target
                    using var sourceBitmap = CanvasBitmap.CreateFromBytes(
                        device, frameBytes, frameWidth, frameHeight,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized);

                    using var rt = new CanvasRenderTarget(device, frameWidth, frameHeight, 96);
                    using (var ds = rt.CreateDrawingSession())
                    {
                        ds.DrawImage(sourceBitmap);

                        var frameDetections = detections.Where(d => d.FrameIndex == i);
                        foreach (var det in frameDetections)
                        {
                            var color = ColorForLabel(det.Label);
                            float x = Math.Clamp(det.X * frameWidth, 0, frameWidth - 1);
                            float y = Math.Clamp(det.Y * frameHeight, 0, frameHeight - 1);
                            float w = Math.Clamp(det.Width * frameWidth, 1, frameWidth - x);
                            float h = Math.Clamp(det.Height * frameHeight, 1, frameHeight - y);

                            // Bounding box
                            ds.DrawRectangle(new Rect(x, y, w, h), color, strokeWidth: 3f);

                            // Label background + text (above the box if room, else just inside)
                            string labelText = $"{det.Label} {det.Confidence:P0}";
                            using var layout = new CanvasTextLayout(
                                device, labelText, labelFormat,
                                requestedWidth: float.PositiveInfinity,
                                requestedHeight: float.PositiveInfinity);
                            float textW = (float)layout.LayoutBounds.Width + 10;
                            float textH = (float)layout.LayoutBounds.Height + 4;
                            float labelY = y - textH;
                            if (labelY < 0) labelY = y; // clip to inside the box if no room above
                            ds.FillRectangle(x, labelY, textW, textH, color);
                            ds.DrawTextLayout(layout, x + 5, labelY + 1, Windows.UI.Color.FromArgb(255, 0, 0, 0));
                        }
                    }

                    string outPath = Path.Combine(outputDir, $"frame_{i:D4}.png");
                    using (var stream = File.Create(outPath))
                    using (var ras = stream.AsRandomAccessStream())
                    {
                        await rt.SaveAsync(ras, CanvasBitmapFileFormat.Png);
                    }
                    savedPaths.Add(outPath);
                }
                catch (Exception ex)
                {
                    Log.Warn($"BoundingBoxRenderer: frame {i} failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"BoundingBoxRenderer: extraction failed: {ex.Message}");
        }

        return savedPaths;
    }

    /// <summary>
    /// Renders annotated frames into an MP4. Returns the output file path.
    /// </summary>
    public static async Task<string?> RenderAnnotatedVideoAsync(
        string videoPath,
        List<DetectionResult> detections,
        string outputVideoPath,
        int maxFrames = 30,
        int frameWidth = 1280,
        int frameHeight = 720,
        Action<string>? onStatus = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ContosoStudio",
            "bbox_frames_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Log.Info($"Rendering {maxFrames} annotated frames at {frameWidth}x{frameHeight}");
            onStatus?.Invoke("Extracting and annotating frames...");
            var framePaths = await RenderAnnotatedFramesAsync(
                videoPath, detections, tempDir, maxFrames, frameWidth, frameHeight,
                onProgress: (cur, total) => onStatus?.Invoke($"Annotating frame {cur}/{total}..."));

            Log.Info($"Rendered {framePaths.Count} annotated frames");
            if (framePaths.Count == 0)
            {
                onStatus?.Invoke("No frames rendered");
                return null;
            }

            // Per-frame duration from source video
            var srcFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var srcClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(srcFile);
            var totalDuration = srcClip.OriginalDuration;
            var frameDuration = TimeSpan.FromMilliseconds(totalDuration.TotalMilliseconds / framePaths.Count);
            Log.Info($"Frame duration: {frameDuration.TotalMilliseconds:F0}ms, total: {totalDuration.TotalSeconds:F1}s");

            onStatus?.Invoke("Building output video...");
            var composition = new Windows.Media.Editing.MediaComposition();
            foreach (var framePath in framePaths)
            {
                var frameFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(framePath);
                var imgClip = await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(frameFile, frameDuration);
                composition.Clips.Add(imgClip);
            }

            onStatus?.Invoke("Encoding MP4...");
            Directory.CreateDirectory(Path.GetDirectoryName(outputVideoPath)!);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
                Path.GetDirectoryName(outputVideoPath)!);
            var outFile = await folder.CreateFileAsync(
                Path.GetFileName(outputVideoPath),
                Windows.Storage.CreationCollisionOption.ReplaceExisting);

            var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
                Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);

            var renderOp = composition.RenderToFileAsync(outFile,
                Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
            renderOp.Progress = (info, progress) => onStatus?.Invoke($"Encoding: {progress:F0}%");

            var result = await renderOp;
            if (result != Windows.Media.Transcoding.TranscodeFailureReason.None)
            {
                onStatus?.Invoke($"Encoding failed: {result}");
                return null;
            }

            onStatus?.Invoke($"Output video saved: {outFile.Path}");
            return outFile.Path;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Video rendering failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
