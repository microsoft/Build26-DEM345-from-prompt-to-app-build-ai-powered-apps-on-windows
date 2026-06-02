using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VideoStudio.Controls;

public sealed partial class HardwareUsageBars : UserControl
{
    public enum PrimaryDeviceKind { None, Npu, Cpu, Gpu }

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

    public static readonly DependencyProperty PrimaryDeviceProperty =
        DependencyProperty.Register(nameof(PrimaryDevice), typeof(PrimaryDeviceKind), typeof(HardwareUsageBars),
            new PropertyMetadata(PrimaryDeviceKind.None, OnPrimaryDeviceChanged));

    public double CpuValue { get => (double)GetValue(CpuValueProperty); set => SetValue(CpuValueProperty, value); }
    public double GpuValue { get => (double)GetValue(GpuValueProperty); set => SetValue(GpuValueProperty, value); }
    public double NpuValue { get => (double)GetValue(NpuValueProperty); set => SetValue(NpuValueProperty, value); }
    public double MemoryValue { get => (double)GetValue(MemoryValueProperty); set => SetValue(MemoryValueProperty, value); }
    public PrimaryDeviceKind PrimaryDevice
    {
        get => (PrimaryDeviceKind)GetValue(PrimaryDeviceProperty);
        set => SetValue(PrimaryDeviceProperty, value);
    }

    public string CpuPercent => $"{CpuValue:0}%";
    public string GpuPercent => $"{GpuValue:0}%";
    public string NpuPercent => $"{NpuValue:0}%";
    public string MemoryPercent => $"{MemoryValue:0}%";

    public HardwareUsageBars()
    {
        this.InitializeComponent();
        ApplyPrimaryDeviceHighlight();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HardwareUsageBars control)
        {
            control.Bindings.Update();
        }
    }

    private static void OnPrimaryDeviceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HardwareUsageBars control)
        {
            control.ApplyPrimaryDeviceHighlight();
        }
    }

    private void ApplyPrimaryDeviceHighlight()
    {
        if (NpuChip == null) return; // template not loaded yet
        var tealFaint = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x0E, 0x8A, 0x7E));
        var orangeFaint = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xE8, 0x7D, 0x2F));
        var purpleFaint = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x7C, 0x3A, 0xED));
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        NpuChip.Background = PrimaryDevice == PrimaryDeviceKind.Npu ? tealFaint : transparent;
        CpuChip.Background = PrimaryDevice == PrimaryDeviceKind.Cpu ? orangeFaint : transparent;
        GpuChip.Background = PrimaryDevice == PrimaryDeviceKind.Gpu ? purpleFaint : transparent;
    }
}
