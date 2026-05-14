using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace VideoStudio.Services;

public record HardwareSnapshot(
    double PeakCpu,
    double PeakGpu,
    double PeakNpu,
    double PeakMemoryMB,
    DateTime StartTime);

public sealed partial class HardwareMonitor : ObservableObject, IDisposable
{
    private const int MaxHistory = 60;
    private const double NpuDecayPerTick = 5.0;

    private readonly DispatcherQueue? _dispatcherQueue;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    // CPU measurement
    private PerformanceCounter? _cpuCounter;
    private bool _useCpuFallback;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuTimestamp;

    // GPU measurement
    private PerformanceCounter? _gpuCounter;

    // NPU heuristic
    private double _reportedNpuPercent;

    // Recording state
    private bool _isRecording;
    private double _peakCpu;
    private double _peakGpu;
    private double _peakNpu;
    private double _peakMemoryMB;
    private DateTime _recordingStart;

    [ObservableProperty]
    public partial double CpuPercent { get; set; }

    [ObservableProperty]
    public partial double GpuPercent { get; set; }

    [ObservableProperty]
    public partial double NpuPercent { get; set; }

    [ObservableProperty]
    public partial double MemoryUsedMB { get; set; }

    [ObservableProperty]
    public partial double MemoryTotalMB { get; set; }

    [ObservableProperty]
    public partial bool IsMonitoring { get; set; }

    [ObservableProperty]
    public partial bool GpuAvailable { get; set; }

    public List<double> CpuHistory { get; } = new(MaxHistory);
    public List<double> GpuHistory { get; } = new(MaxHistory);
    public List<double> NpuHistory { get; } = new(MaxHistory);

    public event Action? HistoryUpdated;

    public HardwareMonitor(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue;
        InitializeCounters();
    }

    public void Start()
    {
        if (IsMonitoring) return;

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        IsMonitoring = true;

        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsMonitoring) return;

        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _cts?.Dispose();
        _cts = null;
        IsMonitoring = false;
    }

    public HardwareSnapshot StartRecording()
    {
        _isRecording = true;
        _peakCpu = 0;
        _peakGpu = 0;
        _peakNpu = 0;
        _peakMemoryMB = 0;
        _recordingStart = DateTime.UtcNow;

        return new HardwareSnapshot(0, 0, 0, 0, _recordingStart);
    }

    public HardwareSnapshot StopRecording(HardwareSnapshot start)
    {
        _isRecording = false;

        return new HardwareSnapshot(
            _peakCpu,
            _peakGpu,
            _peakNpu,
            _peakMemoryMB,
            start.StartTime);
    }

    /// <summary>
    /// Called by OnnxModelRunner (or similar) to report NPU activity.
    /// Between reports the value decays toward zero each tick.
    /// </summary>
    public void ReportNpuActive(double percent)
    {
        _reportedNpuPercent = Math.Clamp(percent, 0, 100);
    }

    public void Dispose()
    {
        Stop();
        _cpuCounter?.Dispose();
        _gpuCounter?.Dispose();
    }

    private void InitializeCounters()
    {
        // CPU counter
        try
        {
            _cpuCounter = new PerformanceCounter(
                "Processor Information", "% Processor Utility", "_Total");
            _cpuCounter.NextValue(); // prime the counter
        }
        catch
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;
            _useCpuFallback = true;
        }

        if (_useCpuFallback)
        {
            var proc = Process.GetCurrentProcess();
            _lastCpuTime = proc.TotalProcessorTime;
            _lastCpuTimestamp = DateTime.UtcNow;
        }

        // GPU counter
        try
        {
            _gpuCounter = FindGpuCounter();
            _gpuCounter?.NextValue();
            GpuAvailable = _gpuCounter is not null;
        }
        catch
        {
            _gpuCounter?.Dispose();
            _gpuCounter = null;
            GpuAvailable = false;
        }

        // Memory total
        var gcInfo = GC.GetGCMemoryInfo();
        MemoryTotalMB = gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
    }

    private static PerformanceCounter? FindGpuCounter()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                if (!instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    continue;

                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                counter.NextValue();
                return counter;
            }
        }
        catch
        {
            // GPU counters not available on this system
        }

        return null;
    }

    private async System.Threading.Tasks.Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(ct))
            {
                var cpu = ReadCpu();
                var gpu = ReadGpu();
                var npu = ReadNpu();
                var mem = ReadMemory();

                if (_isRecording)
                {
                    _peakCpu = Math.Max(_peakCpu, cpu);
                    _peakGpu = Math.Max(_peakGpu, gpu);
                    _peakNpu = Math.Max(_peakNpu, npu);
                    _peakMemoryMB = Math.Max(_peakMemoryMB, mem);
                }

                RunOnUI(() =>
                {
                    CpuPercent = cpu;
                    GpuPercent = gpu;
                    NpuPercent = npu;
                    MemoryUsedMB = mem;

                    AppendHistory(CpuHistory, cpu);
                    AppendHistory(GpuHistory, gpu);
                    AppendHistory(NpuHistory, npu);

                    HistoryUpdated?.Invoke();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop()
        }
    }

    private double ReadCpu()
    {
        if (!_useCpuFallback && _cpuCounter is not null)
        {
            try
            {
                return Math.Clamp(_cpuCounter.NextValue(), 0, 100);
            }
            catch
            {
                _useCpuFallback = true;
            }
        }

        // Fallback: measure process CPU time delta
        try
        {
            var proc = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            var elapsed = (now - _lastCpuTimestamp).TotalMilliseconds;

            double percent = 0;
            if (elapsed > 0)
                percent = (cpuTime - _lastCpuTime).TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100;

            _lastCpuTime = cpuTime;
            _lastCpuTimestamp = now;

            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private double ReadGpu()
    {
        if (_gpuCounter is null) return 0;

        try
        {
            return Math.Clamp(_gpuCounter.NextValue(), 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private double ReadNpu()
    {
        var value = _reportedNpuPercent;
        // Decay toward zero each tick
        _reportedNpuPercent = Math.Max(0, _reportedNpuPercent - NpuDecayPerTick);
        return Math.Clamp(value, 0, 100);
    }

    private double ReadMemory()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        }
        catch
        {
            return 0;
        }
    }

    private static void AppendHistory(List<double> history, double value)
    {
        if (history.Count >= MaxHistory)
            history.RemoveAt(0);
        history.Add(value);
    }

    private void RunOnUI(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }
}
