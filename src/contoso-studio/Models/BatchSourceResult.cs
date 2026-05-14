using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoStudio.Models;

/// <summary>
/// Per-source row in the batch results matrix. Phase 8.7 — see plan.md.
/// One row per input file; columns are populated as the notebook runs over the file.
/// </summary>
public partial class BatchSourceResult : ObservableObject
{
    public MediaFile Source { get; init; } = null!;
    /// <summary>Index in the batch run (1-based, for display).</summary>
    public int Index { get; init; }

    [ObservableProperty] public partial string Status { get; set; } = "Queued";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial long ElapsedMs { get; set; }
    [ObservableProperty] public partial bool Success { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    /// <summary>Snapshot of every Outputs[pin] across all steps after the run completes.</summary>
    public ObservableCollection<BatchOutputCell> Outputs { get; } = [];
}

/// <summary>Single cell in the batch results matrix — one artifact produced by one step for one source.</summary>
public sealed class BatchOutputCell
{
    public string StepName { get; init; } = string.Empty;
    public string EffectId { get; init; } = string.Empty;
    public string PinId { get; init; } = string.Empty;
    public ArtifactKind Kind { get; init; }
    public string? Path { get; init; }
    /// <summary>Short human-readable summary (e.g. "12 chapters", "3 highlights", "1280x720 video").</summary>
    public string Summary { get; init; } = string.Empty;
}
