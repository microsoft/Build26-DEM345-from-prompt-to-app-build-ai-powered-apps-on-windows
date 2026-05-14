using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;

namespace VideoStudio.Controls;

public sealed partial class SourceCard : UserControl
{
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(SourceCard),
            new PropertyMetadata(null, OnFileNameChanged));

    public static readonly DependencyProperty FileInfoProperty =
        DependencyProperty.Register(nameof(FileInfo), typeof(string), typeof(SourceCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ThumbnailsProperty =
        DependencyProperty.Register(nameof(Thumbnails), typeof(IList<ImageSource>), typeof(SourceCard),
            new PropertyMetadata(null));

    public string FileName { get => (string)GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public string FileInfo { get => (string)GetValue(FileInfoProperty); set => SetValue(FileInfoProperty, value); }
    public IList<ImageSource> Thumbnails { get => (IList<ImageSource>)GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }

    public event EventHandler? ChangeRequested;
    public event EventHandler? ImportRequested;

    public SourceCard()
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

    /// <summary>Set video metadata fields individually.</summary>
    public void SetMetadata(string? resolution, string? duration, string? fileSize, string? fileType)
    {
        ResolutionText.Text = resolution ?? "—";
        DurationText.Text = duration ?? "—";
        FileSizeText.Text = fileSize ?? "—";
        FileTypeText.Text = fileType ?? "—";
    }

    /// <summary>Set the preview thumbnail image.</summary>
    public void SetThumbnail(ImageSource? source)
    {
        ThumbnailImage.Source = source;
    }

    /// <summary>Set the thumbnail strip images.</summary>
    public void SetThumbnailStrip(IList<ImageSource>? thumbnails)
    {
        ThumbnailStrip.Children.Clear();
        if (thumbnails == null) return;
        foreach (var thumb in thumbnails)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(3),
                Width = 56,
                Height = 32,
                Background = new ImageBrush { ImageSource = thumb, Stretch = Stretch.UniformToFill }
            };
            ThumbnailStrip.Children.Add(border);
        }
    }

    private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SourceCard card)
            card.UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        bool hasFile = !string.IsNullOrEmpty(FileName);
        EmptyState.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
        ContentPanel.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        if (hasFile)
            FileNameText.Text = FileName;
    }

    private void OnThumbnailClick(object sender, RoutedEventArgs e)
    {
        // Flyout opens automatically via Button.Flyout
    }

    private void OnChangeClicked(object sender, RoutedEventArgs e) =>
        ChangeRequested?.Invoke(this, EventArgs.Empty);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && IsVideoFile(file.FileType))
            {
                FileName = file.Name;
                SetVideoSource(file.Path);
                ImportRequested?.Invoke(this, EventArgs.Empty);
                break;
            }
        }
    }

    private static bool IsVideoFile(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => true,
        _ => false,
    };
}
