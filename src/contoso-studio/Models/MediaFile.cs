using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoStudio.Models;

public partial class MediaFile : ObservableObject
{
    [ObservableProperty] public partial string FilePath { get; set; }
    [ObservableProperty] public partial string FileName { get; set; }
    [ObservableProperty] public partial string FileSize { get; set; }
    [ObservableProperty] public partial string Duration { get; set; }
    [ObservableProperty] public partial string Resolution { get; set; }
    [ObservableProperty] public partial string FileType { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public ObservableCollection<Effect> AppliedEffects { get; } = [];

    public MediaFile(string filePath)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        FileType = System.IO.Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        var info = new System.IO.FileInfo(filePath);
        FileSize = FormatSize(info.Length);
        Duration = "—";
        Resolution = "—";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
