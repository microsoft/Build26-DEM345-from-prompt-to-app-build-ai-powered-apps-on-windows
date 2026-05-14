using Microsoft.UI.Xaml.Media;

namespace VideoStudio.Models;

/// <summary>
/// Display-friendly wrapper around <see cref="DetectionResult"/> for ListView binding.
/// </summary>
public sealed class DetectionViewItem
{
    public int FrameIndex { get; init; }
    public string Label { get; init; } = "";
    public float Confidence { get; init; }
    public Brush LabelColorBrush { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public string FrameText => $"frame {FrameIndex + 1:D3}";
    public string ConfidenceText => $"{Confidence:P0}";

    public static DetectionViewItem From(DetectionResult d)
    {
        return new DetectionViewItem
        {
            FrameIndex = d.FrameIndex,
            Label = d.Label,
            Confidence = d.Confidence,
            LabelColorBrush = LabelColors.For(d.Label)
        };
    }
}

internal static class LabelColors
{
    private static readonly Windows.UI.Color[] Palette =
    [
        Windows.UI.Color.FromArgb(255, 0, 200, 90),    // green
        Windows.UI.Color.FromArgb(255, 232, 125, 47),  // container orange
        Windows.UI.Color.FromArgb(255, 0, 160, 220),   // cyan
        Windows.UI.Color.FromArgb(255, 220, 70, 70),   // red
        Windows.UI.Color.FromArgb(255, 170, 80, 220),  // purple
        Windows.UI.Color.FromArgb(255, 220, 200, 0),   // yellow
        Windows.UI.Color.FromArgb(255, 14, 138, 126),  // accent teal
    ];

    public static Brush For(string label)
    {
        int idx = System.Math.Abs(label.GetHashCode()) % Palette.Length;
        return new SolidColorBrush(Palette[idx]);
    }
}
