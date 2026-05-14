using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoStudio.Models;

public partial class PipelineStep : ObservableObject
{
    private readonly StringBuilder _logBuilder = new();
    private readonly object _logLock = new();

    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    /// <summary>
    /// Unique per-instance identifier (Phase 8.2). Distinct from <see cref="Id"/>, which is
    /// the *effect* id used by the dispatcher. <see cref="StepId"/> identifies *this node*
    /// in the graph and is the correct key for ArtifactRef, work-dir scoping, and batch
    /// templating. Allows multiple instances of the same effect.
    /// </summary>
    [ObservableProperty] public partial string StepId { get; set; } = System.Guid.NewGuid().ToString("N");
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial EngineType Engine { get; set; }
    [ObservableProperty] public partial string Icon { get; set; } = string.Empty;
    [ObservableProperty] public partial PipelineStepState State { get; set; } = PipelineStepState.Waiting;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Waiting";
    [ObservableProperty] public partial string? InputVideoPath { get; set; }
    [ObservableProperty] public partial string? OutputVideoPath { get; set; }
    [ObservableProperty] public partial long DurationMs { get; set; }
    [ObservableProperty] public partial string LogOutput { get; set; } = string.Empty;
    [ObservableProperty] public partial string ResultSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial double CpuUsage { get; set; }
    [ObservableProperty] public partial double GpuUsage { get; set; }
    [ObservableProperty] public partial double NpuUsage { get; set; }
    [ObservableProperty] public partial double MemoryUsageMB { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Effect-specific options (defaults are fine for steps that don't use them).</summary>
    [ObservableProperty] public partial double ConfidenceThreshold { get; set; } = 0.5;
    [ObservableProperty] public partial int SampleFrameCount { get; set; } = 60;

    /// <summary>Hardware target for inference steps. Auto = best available via WinML EP catalog.</summary>
    [ObservableProperty] public partial Services.HardwarePreference HardwarePreference { get; set; } = Services.HardwarePreference.Auto;

    /// <summary>Transcribe options.</summary>
    [ObservableProperty] public partial Services.WhisperModelSize WhisperModel { get; set; } = Services.WhisperModelSize.Tiny;
    [ObservableProperty] public partial string TranscribeLanguage { get; set; } = "en";
    [ObservableProperty] public partial bool TranscribeTranslate { get; set; }

    /// <summary>Detect-silence options.</summary>
    [ObservableProperty] public partial double SilenceThresholdDb { get; set; } = -40;
    [ObservableProperty] public partial int MinSilenceMs { get; set; } = 500;
    [ObservableProperty] public partial int SilencePadMs { get; set; } = 200;

    /// <summary>Smart-cut options.</summary>
    [ObservableProperty] public partial int MinKeepMs { get; set; } = 200;

    /// <summary>Chapter-markers options.</summary>
    [ObservableProperty] public partial int ChapterTargetCount { get; set; } = 6;

    /// <summary>Highlight-picker options.</summary>
    [ObservableProperty] public partial int HighlightTargetCount { get; set; } = 3;
    [ObservableProperty] public partial int HighlightClipSeconds { get; set; } = 30;

    /// <summary>Human-readable label of the device the step actually used (e.g. "NPU (QNN)" or "CPU"). Empty when never run.</summary>
    [ObservableProperty] public partial string DeviceUsedLabel { get; set; } = string.Empty;

    // ─── Phase 8.2: typed inputs/outputs (additive — legacy fields above stay as compatibility shims) ───

    /// <summary>
    /// Explicit per-pin input wiring. Key = pin id from <see cref="Effect.InputPins"/>.
    /// Empty/missing => GetInput&lt;T&gt; resolver falls back to "nearest prior matching artifact"
    /// (preserves linear behavior). Set by the UI when the user picks a non-default source.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, ArtifactRef> Inputs { get; }
        = new System.Collections.Generic.Dictionary<string, ArtifactRef>();

    /// <summary>
    /// Typed outputs produced by the last run. Key = pin id from <see cref="Effect.OutputPins"/>.
    /// Populated by Execute methods as effects migrate (Phase 8.3+). Legacy <see cref="OutputVideoPath"/>
    /// continues to work in parallel until every effect is migrated.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, Artifact> Outputs { get; }
        = new System.Collections.Generic.Dictionary<string, Artifact>();

    /// <summary>Detection results from the last run, populated by detect-objects step.</summary>
    public System.Collections.Generic.IReadOnlyList<DetectionResult> Detections { get; private set; } = [];

    /// <summary>Transcript result from the last run, populated by transcribe step.</summary>
    public TranscriptResult? Transcript { get; private set; }

    /// <summary>Silence intervals from the last run, populated by detect-silence step.</summary>
    public SilenceResult? Silence { get; private set; }

    /// <summary>Chapter markers from the last run, populated by chapter-markers step.</summary>
    public ChapterResult? Chapters { get; private set; }

    /// <summary>Show notes from the last run, populated by show-notes step.</summary>
    public ShowNotesResult? ShowNotes { get; private set; }

    /// <summary>Highlight clips from the last run, populated by highlight-picker step.</summary>
    public HighlightResult? Highlights { get; private set; }

    /// <summary>Exported clip files from the last run, populated by export-clips step.</summary>
    public System.Collections.Generic.IReadOnlyList<Services.ExportedClip> ExportedClips { get; private set; } = System.Array.Empty<Services.ExportedClip>();

    public void SetDetections(System.Collections.Generic.IReadOnlyList<DetectionResult> detections)
    {
        Detections = detections;
        OnPropertyChanged(nameof(Detections));
        OnPropertyChanged(nameof(HasDetections));
        OnPropertyChanged(nameof(DetectionCount));
    }

    public void SetTranscript(TranscriptResult transcript)
    {
        Transcript = transcript;
        OnPropertyChanged(nameof(Transcript));
        OnPropertyChanged(nameof(HasTranscript));
    }

    public void SetSilence(SilenceResult silence)
    {
        Silence = silence;
        OnPropertyChanged(nameof(Silence));
        OnPropertyChanged(nameof(HasSilence));
        OnPropertyChanged(nameof(SilenceRegionCount));
    }

    public void SetChapters(ChapterResult chapters)
    {
        Chapters = chapters;
        OnPropertyChanged(nameof(Chapters));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(ChapterCount));
    }

    public void SetShowNotes(ShowNotesResult notes)
    {
        ShowNotes = notes;
        OnPropertyChanged(nameof(ShowNotes));
        OnPropertyChanged(nameof(HasShowNotes));
    }

    public void SetHighlights(HighlightResult highlights)
    {
        Highlights = highlights;
        OnPropertyChanged(nameof(Highlights));
        OnPropertyChanged(nameof(HasHighlights));
        OnPropertyChanged(nameof(HighlightCount));
    }

    public void SetExportedClips(System.Collections.Generic.IReadOnlyList<Services.ExportedClip> clips)
    {
        ExportedClips = clips;
        OnPropertyChanged(nameof(ExportedClips));
        OnPropertyChanged(nameof(HasExportedClips));
        OnPropertyChanged(nameof(ExportedClipCount));
    }

    public bool HasDetections => Detections.Count > 0;
    public int DetectionCount => Detections.Count;
    public bool HasTranscript => Transcript != null;
    public bool HasSilence => Silence != null;
    public int SilenceRegionCount => Silence?.Intervals.Count ?? 0;
    public bool HasChapters => Chapters != null && Chapters.Chapters.Count > 0;
    public int ChapterCount => Chapters?.Chapters.Count ?? 0;
    public bool HasShowNotes => ShowNotes != null && ShowNotes.HasContent;
    public bool HasHighlights => Highlights != null && Highlights.Highlights.Count > 0;
    public int HighlightCount => Highlights?.Highlights.Count ?? 0;
    public bool HasExportedClips => ExportedClips.Count > 0;
    public int ExportedClipCount => ExportedClips.Count;
    public bool HasDeviceUsed => !string.IsNullOrEmpty(DeviceUsedLabel);

    partial void OnDeviceUsedLabelChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceUsed));

    public string EngineBadge => Engine switch
    {
        EngineType.LinuxContainer => "🐧 Linux Container",
        EngineType.WindowsML => "🧠 Windows ML",
        EngineType.WindowsAI => "✨ Windows AI",
        _ => "Unknown"
    };

    public string DurationText => DurationMs switch
    {
        0 => string.Empty,
        < 60_000 => $"{DurationMs / 1000.0:F1}s",
        _ => $"{DurationMs / 60_000}m {DurationMs % 60_000 / 1000}s"
    };

    public string StateIcon => State switch
    {
        PipelineStepState.Waiting => "\uE823",
        PipelineStepState.Running => "\uE768",
        PipelineStepState.Done => "\uE73E",
        PipelineStepState.Error => "\uEA39",
        _ => string.Empty
    };

    public bool HasOutput => OutputVideoPath is not null;
    public bool HasResult => !string.IsNullOrEmpty(ResultSummary);
    public bool IsRunning => State == PipelineStepState.Running;
    public bool IsDone => State == PipelineStepState.Done;

    partial void OnStateChanged(PipelineStepState value)
    {
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
    }

    partial void OnEngineChanged(EngineType value) =>
        OnPropertyChanged(nameof(EngineBadge));

    partial void OnDurationMsChanged(long value) =>
        OnPropertyChanged(nameof(DurationText));

    partial void OnOutputVideoPathChanged(string? value) =>
        OnPropertyChanged(nameof(HasOutput));

    partial void OnResultSummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasResult));

    public void AppendLog(string line)
    {
        lock (_logLock)
        {
            _logBuilder.AppendLine(line);
            LogOutput = _logBuilder.ToString();
        }
    }

    public static PipelineStep FromEffect(Effect effect) => new()
    {
        Id = effect.Id,
        Name = effect.Name,
        Description = effect.Description,
        Engine = effect.Engine,
        Icon = effect.Icon
    };
}
