using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Pure CPU/DSP silence detection on PCM 16-bit mono 16kHz audio.
/// Window-based RMS thresholding in dBFS.
/// </summary>
public static class SilenceDetector
{
    private const int SampleRate = 16000;
    private const int WindowMs = 30;
    private const int HopMs = 10;

    /// <summary>
    /// Detect silent intervals from an audio/video file.
    /// </summary>
    public static async Task<SilenceResult> DetectAsync(
        string inputPath,
        double thresholdDb = -40,
        int minSilenceMs = 500,
        int padMs = 200,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        onStatus?.Invoke("Extracting audio for silence analysis...");
        var pcm = await AudioExtractor.ExtractPcm16Mono16kAsync(inputPath, onStatus, ct);
        if (pcm.Length == 0)
            throw new InvalidOperationException("Audio extraction produced no samples.");

        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);
        double durationSec = samples.Length / (double)SampleRate;

        onStatus?.Invoke($"Analyzing {durationSec:F1}s @{thresholdDb:F0} dBFS, min={minSilenceMs}ms, pad={padMs}ms");

        var sw = Stopwatch.StartNew();
        var intervals = await Task.Run(() => DetectInternal(samples, thresholdDb, minSilenceMs, padMs), ct);
        sw.Stop();

        double total = 0;
        foreach (var iv in intervals) total += iv.Duration;

        onStatus?.Invoke($"✓ Found {intervals.Count} silent regions, {total:F1}s total ({(durationSec > 0 ? total / durationSec * 100 : 0):F1}% of audio)");

        return new SilenceResult
        {
            Intervals = intervals,
            TotalSilenceSec = total,
            AudioDurationSec = durationSec,
            ElapsedMs = sw.ElapsedMilliseconds,
            DeviceUsed = "CPU (DSP)",
            ThresholdDb = thresholdDb,
            MinSilenceMs = minSilenceMs,
            PadMs = padMs
        };
    }

    private static List<SilenceInterval> DetectInternal(short[] samples, double thresholdDb, int minSilenceMs, int padMs)
    {
        int windowSize = SampleRate * WindowMs / 1000;
        int hopSize = SampleRate * HopMs / 1000;
        if (windowSize < 1 || hopSize < 1) return new();

        // Use a power threshold instead of dB to avoid log per-window.
        // dB = 20*log10(rms/32767)  =>  rms = 32767 * 10^(db/20)
        double rmsThresh = 32767.0 * Math.Pow(10, thresholdDb / 20.0);
        double powerThresh = rmsThresh * rmsThresh;

        int totalWindows = Math.Max(0, (samples.Length - windowSize) / hopSize + 1);
        if (totalWindows <= 0) return new();

        var silentWindow = new bool[totalWindows];
        for (int w = 0; w < totalWindows; w++)
        {
            int start = w * hopSize;
            long sumSq = 0;
            for (int i = 0; i < windowSize; i++)
            {
                int s = samples[start + i];
                sumSq += s * s;
            }
            double meanSq = sumSq / (double)windowSize;
            silentWindow[w] = meanSq < powerThresh;
        }

        // Group contiguous silent windows; convert to seconds.
        var raw = new List<SilenceInterval>();
        int runStart = -1;
        for (int w = 0; w < totalWindows; w++)
        {
            if (silentWindow[w])
            {
                if (runStart < 0) runStart = w;
            }
            else if (runStart >= 0)
            {
                raw.Add(WindowsToInterval(runStart, w, hopSize, windowSize));
                runStart = -1;
            }
        }
        if (runStart >= 0)
            raw.Add(WindowsToInterval(runStart, totalWindows, hopSize, windowSize));

        // Apply min duration and inward padding.
        double minDur = minSilenceMs / 1000.0;
        double pad = padMs / 1000.0;
        var result = new List<SilenceInterval>(raw.Count);
        foreach (var iv in raw)
        {
            if (iv.Duration < minDur) continue;
            double s = iv.Start + pad;
            double e = iv.End - pad;
            if (e - s <= 0) continue;
            result.Add(new SilenceInterval { Start = s, End = e });
        }
        return result;
    }

    private static SilenceInterval WindowsToInterval(int firstWindow, int endWindowExclusive, int hopSize, int windowSize)
    {
        double start = firstWindow * hopSize / (double)SampleRate;
        double end = ((endWindowExclusive - 1) * hopSize + windowSize) / (double)SampleRate;
        return new SilenceInterval { Start = start, End = end };
    }
}
