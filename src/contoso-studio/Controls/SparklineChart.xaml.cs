using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace VideoStudio.Controls;

public sealed partial class SparklineChart : UserControl
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IList<double>), typeof(SparklineChart),
            new PropertyMetadata(null, OnPointsChanged));

    public static readonly DependencyProperty StrokeColorProperty =
        DependencyProperty.Register(nameof(StrokeColor), typeof(Color), typeof(SparklineChart),
            new PropertyMetadata(default(Color), OnStrokeColorChanged));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(SparklineChart),
            new PropertyMetadata(1.5, OnStrokeThicknessChanged));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(SparklineChart),
            new PropertyMetadata(100.0, OnPointsChanged));

    public IList<double> Points { get => (IList<double>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Color StrokeColor { get => (Color)GetValue(StrokeColorProperty); set => SetValue(StrokeColorProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    public SparklineChart()
    {
        this.InitializeComponent();
        this.SizeChanged += (_, _) => UpdateChart();
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineChart chart)
        {
            chart.UpdateChart();
        }
    }

    private static void OnStrokeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineChart chart && e.NewValue is Color color && color != default)
        {
            chart.ChartLine.Stroke = new SolidColorBrush(color);
        }
    }

    private static void OnStrokeThicknessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SparklineChart chart && e.NewValue is double thickness)
        {
            chart.ChartLine.StrokeThickness = thickness;
        }
    }

    private void UpdateChart()
    {
        var points = Points;
        if (points == null || points.Count < 2)
        {
            ChartLine.Points.Clear();
            return;
        }

        double width = ChartCanvas.ActualWidth;
        double height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double maxVal = MaxValue > 0 ? MaxValue : 100.0;
        int count = points.Count;
        double xStep = width / (count - 1);

        var polyPoints = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double x = i * xStep;
            double y = height - (Math.Clamp(points[i], 0, maxVal) / maxVal * height);
            polyPoints.Add(new Windows.Foundation.Point(x, y));
        }

        ChartLine.Points = polyPoints;
    }
}
