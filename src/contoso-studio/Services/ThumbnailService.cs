using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VideoStudio.Services;

/// <summary>
/// Extracts video thumbnails as WriteableBitmap objects for the pipeline UI
/// (source card thumbnail strip and per-step input/output previews).
/// </summary>
public static class ThumbnailService
{
    /// <summary>
    /// Extracts <paramref name="count"/> evenly-spaced frames from the video and
    /// returns them as WriteableBitmaps suitable for binding in XAML.
    /// Returns an empty list on any error.
    /// </summary>
    public static async Task<List<WriteableBitmap>> ExtractThumbnailStripAsync(
        string videoPath, int count = 6, int width = 120, int height = 68)
    {
        var bitmaps = new List<WriteableBitmap>();

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);

            var duration = clip.OriginalDuration;
            double intervalMs = duration.TotalMilliseconds / count;

            for (int i = 0; i < count; i++)
            {
                var timeOffset = TimeSpan.FromMilliseconds(i * intervalMs);
                var bitmap = await ExtractFrameAsBitmapAsync(composition, timeOffset, width, height);
                if (bitmap != null)
                {
                    bitmaps.Add(bitmap);
                }
            }
        }
        catch
        {
            // Swallow — callers receive whatever frames were collected so far
        }

        return bitmaps;
    }

    /// <summary>
    /// Extracts a single frame at the given offset (default: 25 % of duration).
    /// Returns null on error.
    /// </summary>
    public static async Task<WriteableBitmap?> ExtractSingleThumbnailAsync(
        string videoPath, TimeSpan? timeOffset = null, int width = 240, int height = 135)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);

            var offset = timeOffset ?? TimeSpan.FromMilliseconds(clip.OriginalDuration.TotalMilliseconds * 0.25);

            return await ExtractFrameAsBitmapAsync(composition, offset, width, height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shared helper: grabs a single frame from a <see cref="Windows.Media.Editing.MediaComposition"/>
    /// and converts it to a <see cref="WriteableBitmap"/>.
    /// </summary>
    private static async Task<WriteableBitmap?> ExtractFrameAsBitmapAsync(
        Windows.Media.Editing.MediaComposition composition, TimeSpan timeOffset, int width, int height)
    {
        try
        {
            var thumbnail = await composition.GetThumbnailAsync(
                timeOffset, width, height,
                Windows.Media.Editing.VideoFramePrecision.NearestKeyFrame);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(thumbnail);

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)width,
                ScaledHeight = (uint)height
            };
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            byte[] pixels = pixelData.DetachPixelData();

            var bitmap = new WriteableBitmap(width, height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                await stream.WriteAsync(pixels, 0, pixels.Length);
            }
            bitmap.Invalidate();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
