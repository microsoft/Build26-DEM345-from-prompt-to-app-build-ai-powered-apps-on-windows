using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;

namespace VideoStudio.Controls;

public sealed partial class PipelineOutputCard : UserControl
{
    public static readonly DependencyProperty OutputPathProperty =
        DependencyProperty.Register(nameof(OutputPath), typeof(string), typeof(PipelineOutputCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HasOutputProperty =
        DependencyProperty.Register(nameof(HasOutput), typeof(bool), typeof(PipelineOutputCard),
            new PropertyMetadata(false, OnHasOutputChanged));

    public string OutputPath { get => (string)GetValue(OutputPathProperty); set => SetValue(OutputPathProperty, value); }
    public bool HasOutput { get => (bool)GetValue(HasOutputProperty); set => SetValue(HasOutputProperty, value); }

    public event EventHandler? OpenFolderRequested;
    public event EventHandler? ExportRequested;

    public PipelineOutputCard()
    {
        this.InitializeComponent();
        UpdateVisualState();
    }

    public void SetVideoSource(string path)
    {
        try
        {
            PlayerElement.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch
        {
            PlayerElement.Source = null;
        }
    }

    private static void OnHasOutputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PipelineOutputCard card)
        {
            card.UpdateVisualState();
        }
    }

    private void UpdateVisualState()
    {
        EmptyState.Visibility = HasOutput ? Visibility.Collapsed : Visibility.Visible;
        ContentPanel.Visibility = HasOutput ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        OpenFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        ExportRequested?.Invoke(this, EventArgs.Empty);
    }
}
