using System.Collections.Generic;

namespace VideoStudio.Models;

public class SilenceInterval
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Duration => End - Start;

    public static string FormatTime(double seconds)
    {
        int total = (int)seconds;
        int m = total / 60;
        int s = total % 60;
        int ms = (int)((seconds - total) * 1000);
        return $"{m:D2}:{s:D2}.{ms:D3}";
    }

    public static string FormatDuration(double seconds) => $"{seconds:F2}s";
}

public class SilenceResult
{
    public List<SilenceInterval> Intervals { get; set; } = new();
    public double TotalSilenceSec { get; set; }
    public double AudioDurationSec { get; set; }
    public double Coverage => AudioDurationSec > 0 ? TotalSilenceSec / AudioDurationSec : 0;
    public long ElapsedMs { get; set; }
    public string DeviceUsed { get; set; } = "CPU";
    public double ThresholdDb { get; set; }
    public int MinSilenceMs { get; set; }
    public int PadMs { get; set; }
}
