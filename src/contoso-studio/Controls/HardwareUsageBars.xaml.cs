using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VideoStudio.Controls;

public sealed partial class HardwareUsageBars : UserControl
{
    public static readonly DependencyProperty CpuValueProperty =
        DependencyProperty.Register(nameof(CpuValue), typeof(double), typeof(HardwareUsageBars),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty GpuValueProperty =
        DependencyProperty.Register(nameof(GpuValue), typeof(double), typeof(HardwareUsageBars),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty NpuValueProperty =
        DependencyProperty.Register(nameof(NpuValue), typeof(double), typeof(HardwareUsageBars),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty MemoryValueProperty =
        DependencyProperty.Register(nameof(MemoryValue), typeof(double), typeof(HardwareUsageBars),
            new PropertyMetadata(0.0, OnValueChanged));

    public double CpuValue { get => (double)GetValue(CpuValueProperty); set => SetValue(CpuValueProperty, value); }
    public double GpuValue { get => (double)GetValue(GpuValueProperty); set => SetValue(GpuValueProperty, value); }
    public double NpuValue { get => (double)GetValue(NpuValueProperty); set => SetValue(NpuValueProperty, value); }
    public double MemoryValue { get => (double)GetValue(MemoryValueProperty); set => SetValue(MemoryValueProperty, value); }

    // Formatted percentage strings for display
    public string CpuPercent => $"{CpuValue:0}%";
    public string GpuPercent => $"{GpuValue:0}%";
    public string NpuPercent => $"{NpuValue:0}%";
    public string MemoryPercent => $"{MemoryValue:0}%";

    public HardwareUsageBars()
    {
        this.InitializeComponent();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HardwareUsageBars control)
        {
            control.Bindings.Update();
        }
    }
}
