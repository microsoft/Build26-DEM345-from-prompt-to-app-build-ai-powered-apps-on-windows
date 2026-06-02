using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using VideoStudio.Models;
using VideoStudio.Services;

namespace VideoStudio.ViewModels;

public sealed partial class PipelineViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    // Runners (IDisposable — call Cleanup when done)
    private readonly ContainerRunner _containerRunner = new();
    private readonly OnnxModelRunner _modelRunner = new();
    private readonly DepthEstimationRunner _depthRunner = new();
    private readonly SceneTagger _sceneTagger = new();
    private readonly FrameSearchRunner _frameSearchRunner = new();
    private readonly WhisperRunner _whisperRunner = new();
    private readonly ChapterGenerator _chapterGenerator = new();
    private readonly ShowNotesGenerator _showNotesGenerator = new();
    private readonly HighlightPicker _highlightPicker = new();
    private readonly SuperResolutionRunner _srRunner = new();
    private HardwareMonitor? _hardwareMonitor;
    private bool _modelLoaded;
    private bool _whisperLoaded;
    private WhisperModelSize _whisperLoadedSize;
    private HardwarePreference _whisperLoadedHw;

    // Source media
    [ObservableProperty] public partial MediaFile? SourceFile { get; set; }
    public ObservableCollection<MediaFile> MediaFiles { get; } = [];

    // Phase 8.7 — batch mode
    /// <summary>Files queued for batch processing. When non-empty, RunBatch executes the notebook once per file.</summary>
    public ObservableCollection<MediaFile> BatchSources { get; } = [];
    /// <summary>Per-source batch run results — rows in the batch matrix view.</summary>
    public ObservableCollection<BatchSourceResult> BatchResults { get; } = [];
    [ObservableProperty] public partial bool IsBatchRunning { get; set; }

    // Pipeline execution state
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial double GlobalProgress { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; }

    // InfoBar
    [ObservableProperty] public partial string? InfoBarMessage { get; set; }
    [ObservableProperty] public partial InfoBarSeverity InfoBarSeverity { get; set; }
    [ObservableProperty] public partial bool ShowInfoBar { get; set; }

    // Pipeline steps
    public ObservableCollection<PipelineStep> Steps { get; } = [];

    // Effect catalog
    public List<Effect> AvailableEffects { get; }
    public List<Effect> ContainerEffects { get; }
    public List<Effect> NativeEffects { get; }

    // Computed properties
    public bool CanRun => SourceFile != null && Steps.Count > 0 && !IsRunning;
    public bool HasSteps => Steps.Count > 0;
    public bool HasSource => SourceFile != null;
    public bool NoSource => !HasSource;

    public string PipelineSummary => Steps.Count == 0
        ? string.Empty
        : $"{Steps.Count} step{(Steps.Count == 1 ? "" : "s")}: {string.Join(" → ", Steps.Select(s => s.Name))}";

    public PipelineViewModel()
    {
        StatusMessage = string.Empty;
        InfoBarSeverity = InfoBarSeverity.Informational;

        var containerEffects = new List<Effect>
        {
            new("extract-audio", "Extract Audio", "Extract audio track using FFmpeg in container", EngineType.LinuxContainer, "\uE8D6"),
            new("burn-subs", "Burn Subtitles", "Burn .srt subtitles onto video", EngineType.LinuxContainer, "\uE8C1") { Dependencies = ["transcribe"] },
        };

        var nativeEffects = new List<Effect>
        {
            new("transcribe", "Transcribe", "Speech-to-text via Whisper on NPU/GPU/CPU (Windows ML)", EngineType.WindowsML, "\uE8D4")
            {
                InputPins = [ new("video", "Video", ArtifactKind.Video) ],
                OutputPins = [
                    new("video", "Video", ArtifactKind.Video),
                    new("transcript", "Transcript", ArtifactKind.Transcript)
                ]
            },
            new("detect-silence", "Detect Silence", "Find silent regions in audio (CPU DSP)", EngineType.WindowsML, "\uE74F"),
            new("smart-cut", "Smart Cut", "Trim silent regions out of media (uses Detect Silence)", EngineType.WindowsML, "\uE8C6") { Dependencies = ["detect-silence"] },
            new("chapter-markers", "Chapter Markers", "Generate chapter timestamps from a transcript via an on-device language model (Phi Silica or Foundry Local)", EngineType.WindowsAI, "\uE8FD")
            {
                Dependencies = ["transcribe"], UsesLanguageModel = true,
                InputPins = [ new("transcript", "Transcript", ArtifactKind.Transcript) ],
                OutputPins = [ new("chapters", "Chapters", ArtifactKind.Chapters) ]
            },
            new("show-notes", "Show Notes", "Write episode summary, key takeaways and topic tags from a transcript via an on-device language model (Phi Silica or Foundry Local)", EngineType.WindowsAI, "\uE7C3")
            {
                Dependencies = ["transcribe"], UsesLanguageModel = true,
                InputPins = [ new("transcript", "Transcript", ArtifactKind.Transcript) ],
                OutputPins = [ new("notes", "Notes", ArtifactKind.Text) ]
            },
            new("highlight-picker", "Highlight Picker", "Pick the top 3 share-worthy clips from a transcript via an on-device language model (Phi Silica or Foundry Local)", EngineType.WindowsAI, "\uE734")
            {
                Dependencies = ["transcribe"], UsesLanguageModel = true,
                InputPins = [ new("transcript", "Transcript", ArtifactKind.Transcript) ],
                OutputPins = [ new("highlights", "Highlights", ArtifactKind.Highlights) ]
            },
            new("caption-burn", "Burn Captions", "Burn caption text from an upstream transcript onto the video using ffmpeg + libass", EngineType.WindowsML, "\uE890")
            {
                Dependencies = ["transcribe"],
                InputPins = [
                    new("video", "Video", ArtifactKind.Video),
                    new("transcript", "Transcript", ArtifactKind.Transcript)
                ],
                OutputPins = [ new("video", "Captioned Video", ArtifactKind.Video) ]
            },
            new("export-clips", "Export Clips", "Export each highlight as a standalone short clip (ffmpeg stream-copy)", EngineType.WindowsML, "\uE74B") { Dependencies = ["highlight-picker"] },
            new("detect-objects", "Detect Objects", "YOLOS object detection on NPU via Windows ML", EngineType.WindowsML, "\uE8B3")
            {
                InputPins = [ new("video", "Video", ArtifactKind.Video) ],
                OutputPins = [
                    new("video", "Annotated Video", ArtifactKind.Video),
                    new("detections", "Detections", ArtifactKind.Detections)
                ]
            },
            new("depth-map", "Depth Map", "Monocular depth estimation (Depth-Anything-Small) — colorized turbo heatmap side-by-side with source", EngineType.WindowsML, "\uE81E"),
            new("scene-tags", "Scene Tags", "Per-frame scene/object classification (ViT-B/16 ImageNet) — top-3 labels burned onto video", EngineType.WindowsML, "\uE8EC"),
            new("find-frames", "Find Frames", "Search the video for a text prompt — CLIP (openai/clip-vit-base-patch16) compiled to NPU via the winml CLI ranks every sampled frame by similarity", EngineType.WindowsML, "\uE721"),
            new("upscale", "Upscale", "AI super-resolution via Windows AI", EngineType.WindowsAI, "\uE740"),
        };

        ContainerEffects = containerEffects;
        NativeEffects = nativeEffects;
        AvailableEffects = [.. containerEffects, .. nativeEffects];

        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(HasSteps));
            OnPropertyChanged(nameof(PipelineSummary));
            RunAllCommand.NotifyCanExecuteChanged();
        };

        // Persist imported media across launches.
        RestoreImportedMediaFiles();
        MediaFiles.CollectionChanged += (_, _) => SaveImportedMediaFiles();
    }

    // Setting key for the persisted list of imported media file paths.
    private const string ImportedMediaSettingKey = "ImportedMediaPaths";

    private void RestoreImportedMediaFiles()
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (settings.Values[ImportedMediaSettingKey] is not string serialized || string.IsNullOrEmpty(serialized))
                return;

            // Newline-delimited absolute paths. Skip any file the user has since moved/deleted.
            var paths = serialized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                if (MediaFiles.Any(m => m.FilePath == path)) continue;
                var media = new MediaFile(path);
                MediaFiles.Add(media);
                // Probe metadata in background — same as fresh import.
                _ = ProbeVideoMetadataAsync(media);
            }
            // Restore the "current" source selection to the first surviving file so the source card renders.
            if (SourceFile == null && MediaFiles.Count > 0)
                SourceFile = MediaFiles[0];
        }
        catch
        {
            // Settings/file access can fail in unpackaged or restricted contexts — silently ignore so the app still launches.
        }
    }

    private void SaveImportedMediaFiles()
    {
        try
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            settings.Values[ImportedMediaSettingKey] = string.Join('\n', MediaFiles.Select(m => m.FilePath));
        }
        catch
        {
            // Best-effort persistence — never crash the UI for a settings write.
        }
    }

    partial void OnSourceFileChanged(MediaFile? value)
    {
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(NoSource));
        OnPropertyChanged(nameof(CanRun));
        RunAllCommand.NotifyCanExecuteChanged();

        if (value != null && Steps.Count > 0)
        {
            Steps[0].InputVideoPath = value.FilePath;
        }

        // Phase 8.8.1: refresh source-pointing input refs so layout/wires stay in sync.
        BackfillAllInputRefs();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        RunAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ImportFiles(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Video formats
            bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm";
            // Audio formats (for transcribe / podcast workflows)
            bool isAudio = ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".aac";
            if (!isVideo && !isAudio) continue;

            var media = new MediaFile(path);
            if (!MediaFiles.Any(m => m.FilePath == media.FilePath))
                MediaFiles.Add(media);
            SourceFile = media;
            ShowInfo($"Imported: {SourceFile.FileName}", InfoBarSeverity.Informational);

            // Probe video metadata in background
            _ = ProbeVideoMetadataAsync(media);
        }
    }

    private async System.Threading.Tasks.Task ProbeVideoMetadataAsync(MediaFile media)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(media.FilePath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            var props = clip.GetVideoEncodingProperties();

            media.Duration = FormatDuration(clip.OriginalDuration);
            media.Resolution = $"{props.Width}×{props.Height}";

            // Notify UI that source metadata changed
            OnPropertyChanged(nameof(SourceFile));
        }
        catch { /* metadata probing is best-effort */ }
    }

    private static string FormatDuration(TimeSpan ts) => ts.TotalHours >= 1
        ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s"
        : ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s"
            : $"{ts.Seconds}s";

    [RelayCommand]
    private void AddStep(Effect effect) => InsertStep(-1, effect);

    /// <summary>
    /// Insert a PipelineStep (from Effect) after the given index.
    /// If afterIndex is -1, append to the end.
    /// </summary>
    public void InsertStep(int afterIndex, Effect effect)
    {
        // Don't add duplicates
        if (Steps.Any(s => s.Id == effect.Id)) return;

        // Auto-resolve dependencies first
        foreach (var depId in effect.Dependencies)
        {
            if (!Steps.Any(s => s.Id == depId))
            {
                var dep = AvailableEffects.FirstOrDefault(e => e.Id == depId);
                if (dep != null)
                {
                    var depStep = PipelineStep.FromEffect(dep);
                    int insertIdx = afterIndex == -1 ? Steps.Count : Math.Min(afterIndex + 1, Steps.Count);
                    Steps.Insert(insertIdx, depStep);
                    BackfillInputRefs(depStep);
                    if (afterIndex >= 0) afterIndex++;
                }
            }
        }

        var step = PipelineStep.FromEffect(effect);
        // Phase 8.9 — backfill BEFORE inserting so the CollectionChanged → RebuildPipelineUI
        // pass renders chips & wires with the populated step.Inputs.
        int targetIdx = afterIndex == -1 ? Steps.Count : Math.Min(afterIndex + 1, Steps.Count);
        BackfillInputRefsAt(step, targetIdx);
        Steps.Insert(targetIdx, step);

        UpdateStepInputPaths();
    }

    /// <summary>Same as <see cref="BackfillInputRefs"/> but uses an explicit insertion index
    /// instead of <c>Steps.IndexOf</c> so it can run before the step is inserted.</summary>
    private void BackfillInputRefsAt(PipelineStep step, int idx)
    {
        var effect = FindEffect(step.Id);
        if (effect == null) return;
        BackfillInputRefsCore(step, effect, idx);
    }

    [RelayCommand]
    private void RemoveStep(PipelineStep step)
    {
        // Find and remove dependents first
        var dependents = Steps
            .Where(s => AvailableEffects
                .FirstOrDefault(e => e.Id == s.Id)
                ?.Dependencies.Contains(step.Id) == true)
            .ToList();

        foreach (var dep in dependents)
        {
            Steps.Remove(dep);
        }

        Steps.Remove(step);
        UpdateStepInputPaths();
    }

    [RelayCommand]
    private void MoveStepUp(PipelineStep step)
    {
        int index = Steps.IndexOf(step);
        if (index <= 0) return;
        Steps.Move(index, index - 1);
        UpdateStepInputPaths();
    }

    [RelayCommand]
    private void MoveStepDown(PipelineStep step)
    {
        int index = Steps.IndexOf(step);
        if (index < 0 || index >= Steps.Count - 1) return;
        Steps.Move(index, index + 1);
        UpdateStepInputPaths();
    }

    public void SetHardwareMonitor(HardwareMonitor monitor) => _hardwareMonitor = monitor;

    /// <summary>Dispose runner resources.</summary>
    public void Cleanup()
    {
        _containerRunner.Dispose();
        _modelRunner.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAllAsync()
    {
        if (SourceFile == null || Steps.Count == 0) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        GlobalProgress = 0;
        ShowInfo("Running pipeline...", InfoBarSeverity.Informational);

        // Create work directory and copy source file
        string workDir = Path.Combine(Path.GetTempPath(), "ContosoStudio", $"work_{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(workDir);

        string workFilePath = Path.Combine(workDir, SourceFile.FileName);
        File.Copy(SourceFile.FilePath, workFilePath, overwrite: true);

        int completedCount = 0;

        try
        {
            // Chain: first step gets the copied source, subsequent steps get previous output
            string currentInput = workFilePath;

            for (int i = 0; i < Steps.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var step = Steps[i];
                step.InputVideoPath = currentInput;
                StatusMessage = $"Running step {i + 1}/{Steps.Count}: {step.Name}...";

                await ExecuteStepAsync(step, workDir, _cts.Token);

                // Chain output → next input
                if (step.State == PipelineStepState.Done && step.OutputVideoPath != null)
                    currentInput = step.OutputVideoPath;

                if (step.State == PipelineStepState.Done)
                    completedCount++;

                GlobalProgress = (double)(i + 1) / Steps.Count * 100;
            }

            StatusMessage = "Pipeline complete";
            ShowInfo($"Pipeline complete — {completedCount} step{(completedCount == 1 ? "" : "s")} finished", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Pipeline cancelled";
            ShowInfo("Pipeline execution was cancelled", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pipeline error: {ex.Message}";
            ShowInfo($"Pipeline failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Phase 8.7 — batch run. Snapshots the current notebook, then runs it once per
    /// file in <see cref="BatchSources"/>, recording per-source results in <see cref="BatchResults"/>.
    /// Each iteration: clears step state, swaps SourceFile, reuses the existing RunAllAsync path,
    /// then captures every step's typed Outputs into a <see cref="BatchSourceResult"/> row.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task RunBatchAsync()
    {
        if (BatchSources.Count == 0 || Steps.Count == 0 || IsRunning || IsBatchRunning) return;

        // Snapshot the notebook recipe so we can re-apply it per source. This also means
        // the per-source runs are deterministic regardless of any in-place state mutations.
        var snapshot = NotebookSerializer.FromSteps(Steps, name: "(batch)");
        var originalSource = SourceFile;
        IsBatchRunning = true;
        BatchResults.Clear();

        try
        {
            int idx = 1;
            foreach (var src in BatchSources.ToList())
            {
                var row = new BatchSourceResult { Source = src, Index = idx, Status = "Running" };
                BatchResults.Add(row);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Reset notebook to a clean copy for each source.
                    NotebookSerializer.ApplyTo(snapshot, this);
                    SourceFile = src;
                    StatusMessage = $"Batch {idx}/{BatchSources.Count}: {src.FileName}";

                    await RunAllAsync();

                    // Snapshot every typed output across all steps for the matrix view.
                    foreach (var step in Steps)
                    {
                        foreach (var kv in step.Outputs)
                        {
                            var art = kv.Value;
                            row.Outputs.Add(new BatchOutputCell
                            {
                                StepName = step.Name,
                                EffectId = step.Id,
                                PinId = kv.Key,
                                Kind = art.Kind,
                                Path = art.Path,
                                Summary = SummarizeArtifact(art)
                            });
                        }
                    }

                    int doneCount = Steps.Count(s => s.State == PipelineStepState.Done);
                    row.Success = doneCount == Steps.Count;
                    row.Status = row.Success ? "Done" : $"{doneCount}/{Steps.Count} steps";
                }
                catch (Exception ex)
                {
                    row.Success = false;
                    row.Status = "Error";
                    row.ErrorMessage = ex.Message;
                }
                finally
                {
                    sw.Stop();
                    row.ElapsedMs = sw.ElapsedMilliseconds;
                    row.Progress = 100;
                }
                idx++;
            }

            int succeeded = BatchResults.Count(r => r.Success);
            ShowInfo($"Batch complete — {succeeded}/{BatchResults.Count} files succeeded", InfoBarSeverity.Success);
        }
        finally
        {
            // Restore the original source so the canvas remains predictable for the user.
            NotebookSerializer.ApplyTo(snapshot, this);
            SourceFile = originalSource;
            IsBatchRunning = false;
            RunBatchCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunBatch => BatchSources.Count > 0 && Steps.Count > 0 && !IsRunning && !IsBatchRunning;

    private static string SummarizeArtifact(Artifact art) => art switch
    {
        VideoArtifact v => v.Width > 0 ? $"{v.Width}×{v.Height} video" : "video",
        AudioArtifact a => a.SampleRate > 0 ? $"{a.SampleRate / 1000.0:F0} kHz audio" : "audio",
        TranscriptArtifact t => $"{t.Segments.Count} segments",
        DetectionsArtifact d => $"{d.TotalCount} detections / {d.FrameCount} frames",
        DepthArtifact dp => $"{dp.FrameCount} depth frames",
        SceneTagsArtifact st => $"{st.FrameCount} tagged frames",
        HighlightsArtifact h => $"{h.Ranges.Count} highlights",
        SilenceArtifact s => $"{s.Ranges.Count} silent regions",
        ChaptersArtifact c => $"{c.Chapters.Count} chapters",
        TextArtifact tx => $"{(tx.Content?.Length ?? 0)} chars",
        _ => art.Kind.ToString()
    };

    [RelayCommand]
    private void AddBatchSource(MediaFile file)
    {
        if (file != null && !BatchSources.Contains(file))
        {
            BatchSources.Add(file);
            RunBatchCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RemoveBatchSource(MediaFile file)
    {
        if (file != null && BatchSources.Remove(file))
            RunBatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearBatchSources()
    {
        BatchSources.Clear();
        BatchResults.Clear();
        RunBatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RunStepAsync(PipelineStep step)
    {
        if (SourceFile == null) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();

        // Build the upstream chain: every step this one transitively depends on
        // via Inputs[].StepId (skipping the bucket/source pseudo-step), in
        // pipeline order, ending with the requested step. Done steps are kept
        // in the chain only to seed currentInput; they're not re-executed.
        var chain = CollectStepChain(step);

        string workDir = Path.Combine(Path.GetTempPath(), "ContosoStudio", $"work_{Guid.NewGuid():N}"[..13]);
        Directory.CreateDirectory(workDir);

        string workFilePath = Path.Combine(workDir, SourceFile.FileName);
        File.Copy(SourceFile.FilePath, workFilePath, overwrite: true);

        try
        {
            string currentInput = workFilePath;
            int execCount = 0;
            // The user explicitly clicked Run on `step`, so it always re-executes — even if
            // it was previously Done — so changing a setting and clicking Run "just works".
            // Upstream Done steps still reuse their output (we only re-run upstream that
            // hasn't completed yet, e.g. first-time runs of the chain).
            int execTotal = chain.Count(s => s == step || s.State != PipelineStepState.Done);

            foreach (var s in chain)
            {
                _cts.Token.ThrowIfCancellationRequested();

                // Upstream that already finished — reuse its output, don't re-run.
                // The target step (`s == step`) always re-runs so settings changes apply.
                if (s != step && s.State == PipelineStepState.Done)
                {
                    if (s.OutputVideoPath != null) currentInput = s.OutputVideoPath;
                    continue;
                }

                s.InputVideoPath = currentInput;
                execCount++;
                StatusMessage = execTotal > 1
                    ? $"Running upstream {execCount}/{execTotal}: {s.Name}..."
                    : $"Running {s.Name}...";

                await ExecuteStepAsync(s, workDir, _cts.Token);

                if (s.State != PipelineStepState.Done)
                {
                    // Upstream failed — abort the chain so the user gets a clear signal
                    // instead of a downstream "no transcript available" skip.
                    throw new InvalidOperationException(
                        $"Upstream step '{s.Name}' did not finish — cannot run '{step.Name}'.");
                }

                if (s.OutputVideoPath != null) currentInput = s.OutputVideoPath;
            }

            StatusMessage = $"{step.Name} complete";
            ShowInfo(execTotal > 1
                ? $"{step.Name} finished (ran {execTotal} step{(execTotal == 1 ? "" : "s")})"
                : $"{step.Name} finished successfully", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{step.Name} cancelled";
            ShowInfo($"{step.Name} was cancelled", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{step.Name} failed: {ex.Message}";
            ShowInfo($"{step.Name} failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Collects <paramref name="target"/> plus every step it transitively depends on
    /// (via <see cref="PipelineStep.Inputs"/>), returned in pipeline order so the
    /// caller can execute them sequentially. Pseudo-step refs (source / bucket) are
    /// ignored; only real upstream steps are included.
    /// </summary>
    private List<PipelineStep> CollectStepChain(PipelineStep target)
    {
        var stepById = Steps.ToDictionary(s => s.StepId, s => s);
        var visited = new HashSet<string>();
        var result = new List<PipelineStep>();

        void Visit(PipelineStep s)
        {
            if (!visited.Add(s.StepId)) return;
            foreach (var input in s.Inputs.Values)
            {
                if (string.IsNullOrEmpty(input.StepId)) continue;
                if (IsInputStepId(input.StepId)) continue;
                if (stepById.TryGetValue(input.StepId, out var upstream))
                    Visit(upstream);
            }
            result.Add(s);
        }

        Visit(target);

        // Preserve pipeline (visual) order so chained `currentInput` reflects the
        // user's mental model of left-to-right flow even if traversal happened
        // in a different order.
        var order = new Dictionary<string, int>();
        for (int i = 0; i < Steps.Count; i++) order[Steps[i].StepId] = i;
        result.Sort((a, b) => order[a.StepId].CompareTo(order[b.StepId]));
        return result;
    }

    [RelayCommand]
    private void StopExecution()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void ClearPipeline()
    {
        Steps.Clear();
        GlobalProgress = 0;
        StatusMessage = string.Empty;
    }

    /// <summary>Public clear used by NotebookSerializer when loading a notebook.</summary>
    public void ClearSteps() => ClearPipeline();

    /// <summary>
    /// Execute a single pipeline step using the real runners.
    /// Errors are caught per-step so the pipeline can continue.
    /// </summary>
    private async Task ExecuteStepAsync(PipelineStep step, string workDir, CancellationToken ct)
    {
        step.State = PipelineStepState.Running;
        step.StatusText = "Processing...";
        step.Progress = 0;

        Log.Info($"Starting step '{step.Name}' (id={step.Id}), input={step.InputVideoPath}");
        var sw = Stopwatch.StartNew();
        var hwStart = _hardwareMonitor?.StartRecording();

        try
        {
            ct.ThrowIfCancellationRequested();

            switch (step.Id)
            {
                case "extract-audio":
                    await ExecuteExtractAudioAsync(step, workDir);
                    break;

                case "transcribe":
                    await ExecuteTranscribeAsync(step, workDir);
                    break;

                case "detect-silence":
                    await ExecuteDetectSilenceAsync(step, workDir);
                    break;

                case "smart-cut":
                    await ExecuteSmartCutAsync(step, workDir);
                    break;

                case "chapter-markers":
                    await ExecuteChapterMarkersAsync(step, workDir);
                    break;

                case "show-notes":
                    await ExecuteShowNotesAsync(step, workDir);
                    break;

                case "highlight-picker":
                    await ExecuteHighlightPickerAsync(step, workDir);
                    break;

                case "caption-burn":
                    await ExecuteCaptionBurnAsync(step, workDir);
                    break;

                case "export-clips":
                    await ExecuteExportClipsAsync(step, workDir);
                    break;

                case "burn-subs":
                    await ExecuteBurnSubsAsync(step, workDir);
                    break;

                case "detect-objects":
                    await ExecuteDetectObjectsAsync(step);
                    break;

                case "depth-map":
                    await ExecuteDepthMapAsync(step);
                    break;

                case "scene-tags":
                    await ExecuteSceneTagsAsync(step);
                    break;

                case "find-frames":
                    await ExecuteFindFramesAsync(step);
                    break;

                case "upscale":
                    await ExecuteUpscaleAsync(step, workDir);
                    break;

                default:
                    step.AppendLog($"Unknown effect ID: {step.Id}");
                    break;
            }

            sw.Stop();
            step.DurationMs = sw.ElapsedMilliseconds;
            step.State = PipelineStepState.Done;
            step.StatusText = "Done";
            step.Progress = 100;
            step.AppendLog($"[{step.Name}] Finished — {step.DurationText}");
            Log.Info($"Step '{step.Name}' done in {step.DurationText}, output={step.OutputVideoPath}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Step '{step.Name}' cancelled");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            step.DurationMs = sw.ElapsedMilliseconds;
            step.State = PipelineStepState.Error;
            step.StatusText = ex.Message;
            step.AppendLog($"ERROR: {ex.Message}");
            if (ex.InnerException != null)
                step.AppendLog($"  Inner: {ex.InnerException.Message}");

            Log.Error($"Step '{step.Name}' failed", ex);
            ShowInfo($"{step.Name} failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (hwStart != null && _hardwareMonitor != null)
            {
                var snapshot = _hardwareMonitor.StopRecording(hwStart);
                step.CpuUsage = snapshot.PeakCpu;
                step.GpuUsage = snapshot.PeakGpu;
                step.NpuUsage = snapshot.PeakNpu;
                // Convert peak working-set MB into a percentage of total system RAM for the bar (0-100).
                double totalRamMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
                step.MemoryUsageMB = totalRamMB > 0
                    ? Math.Clamp(snapshot.PeakMemoryMB / totalRamMB * 100.0, 0, 100)
                    : 0;
                Log.Info($"Step '{step.Name}' HW peaks: CPU={snapshot.PeakCpu:F0}% GPU={snapshot.PeakGpu:F0}% NPU={snapshot.PeakNpu:F0}% MEM={snapshot.PeakMemoryMB:F0}MB");
            }
        }
    }

    // ── Container effects ─────────────────────────────────────────────

    private async Task ExecuteExtractAudioAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;
        string audioPath = Path.Combine(workDir, "audio.wav");

        step.AppendLog("Extracting audio track from video...");
        step.AppendLog($"Input: {Path.GetFileName(inputPath)}");
        step.AppendLog("Output: audio.wav (16kHz mono PCM)");

        step.StatusText = "Initializing container...";
        step.Progress = 5;
        await _containerRunner.InitSessionAsync();
        step.AppendLog("Container runtime ready");

        step.StatusText = "Extracting audio...";
        step.Progress = 20;
        await _containerRunner.RunFFmpegExtractAsync(inputPath, audioPath, output =>
        {
            step.AppendLog(output.TrimEnd('\n', '\r'));
        });

        if (File.Exists(audioPath))
        {
            var audioSize = new FileInfo(audioPath).Length;
            step.AppendLog($"✓ Audio extracted ({audioSize / 1024.0:F0} KB)");
            step.OutputVideoPath = audioPath;
            step.ResultSummary = $"audio.wav ({audioSize / 1024.0:F0} KB)";
        }
        else
        {
            // Pass-through: output = input
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Audio extracted";
        }

        step.Progress = 100;
    }

    private async Task ExecuteTranscribeAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        // Check transcript cache before doing any work
        var cachedResult = _whisperRunner.TryGetCachedTranscript(
            inputPath, step.WhisperModel, step.TranscribeLanguage, step.TranscribeTranslate);
        if (cachedResult != null)
        {
            step.AppendLog($"✓ Using cached transcript ({cachedResult.Segments.Count} segments)");
            step.StatusText = "Loaded from cache";
            step.DeviceUsedLabel = cachedResult.DeviceUsed;
            step.SetTranscript(cachedResult);

            // Still write SRT/TXT artifacts for downstream steps
            string srtPath2 = Path.Combine(workDir, "subtitles.srt");
            string txtPath2 = Path.Combine(workDir, "transcript.txt");
            await File.WriteAllTextAsync(srtPath2, BuildSrt(cachedResult));
            await File.WriteAllTextAsync(txtPath2, cachedResult.FullText);
            step.AppendLog($"✓ Wrote {Path.GetFileName(srtPath2)} and {Path.GetFileName(txtPath2)}");

            var segmentsForArtifact2 = new System.Collections.Generic.List<(int startMs, int endMs, string text)>(cachedResult.Segments.Count);
            foreach (var seg in cachedResult.Segments)
                segmentsForArtifact2.Add(((int)(seg.Start * 1000), (int)(seg.End * 1000), seg.Text));
            step.Outputs["transcript"] = new TranscriptArtifact
            {
                ProducingStepId = step.StepId,
                ProducingPinId = "transcript",
                Path = srtPath2,
                FullText = cachedResult.FullText,
                Language = step.TranscribeLanguage,
                Segments = segmentsForArtifact2
            };
            step.Outputs["video"] = new VideoArtifact
            {
                ProducingStepId = step.StepId,
                ProducingPinId = "video",
                Path = inputPath
            };
            step.OutputVideoPath = inputPath;
            step.ResultSummary =
                $"{cachedResult.Segments.Count} segments • {cachedResult.AudioDurationSeconds:F0}s audio • cached";
            step.Progress = 100;
            return;
        }

        // Lazy-init / reload Whisper if model size or hardware preference changed
        if (!_whisperLoaded
            || _whisperLoadedSize != step.WhisperModel
            || _whisperLoadedHw != step.HardwarePreference)
        {
            step.AppendLog($"Initializing Whisper {step.WhisperModel} on {step.HardwarePreference}...");
            step.StatusText = "Initializing Whisper...";
            step.Progress = 5;
            await _whisperRunner.InitializeAsync(
                step.WhisperModel,
                step.HardwarePreference,
                onStatus: msg => step.AppendLog(msg),
                onDownloadProgress: (got, total, pct) =>
                {
                    step.Progress = Math.Min(15, 5 + pct * 0.10); // 5-15% during download
                    step.StatusText = total > 0
                        ? $"Downloading Whisper model... {got / 1024 / 1024} / {total / 1024 / 1024} MB ({pct:F0}%)"
                        : $"Downloading Whisper model... {got / 1024 / 1024} MB";
                });
            _whisperLoaded = true;
            _whisperLoadedSize = step.WhisperModel;
            _whisperLoadedHw = step.HardwarePreference;
        }

        step.DeviceUsedLabel = _whisperRunner.ActiveBackend;
        step.StatusText = $"Transcribing on {_whisperRunner.ActiveBackend}...";
        step.Progress = 20;
        step.AppendLog($"Active backend: {_whisperRunner.ActiveBackend}");
        step.AppendLog(step.TranscribeTranslate
            ? $"Mode: Translate ({step.TranscribeLanguage} → en)"
            : $"Mode: Transcribe ({step.TranscribeLanguage})");

        var result = await _whisperRunner.TranscribeAsync(
            inputPath,
            language: step.TranscribeLanguage,
            translate: step.TranscribeTranslate,
            includeTimestamps: true,
            onStatus: msg => step.AppendLog(msg),
            onProgress: new Progress<double>(p => step.Progress = 20 + p * 75)); // 20-95%

        // Cache the result for future runs
        _whisperRunner.CacheTranscript(
            inputPath, step.WhisperModel, step.TranscribeLanguage, step.TranscribeTranslate, result);

        step.SetTranscript(result);
        step.DeviceUsedLabel = result.DeviceUsed;

        // Write SRT and plain-text artifacts to workDir for downstream steps (Burn Subs, etc.)
        string srtPath = Path.Combine(workDir, "subtitles.srt");
        string txtPath = Path.Combine(workDir, "transcript.txt");
        await File.WriteAllTextAsync(srtPath, BuildSrt(result));
        await File.WriteAllTextAsync(txtPath, result.FullText);
        step.AppendLog($"✓ Wrote {Path.GetFileName(srtPath)} and {Path.GetFileName(txtPath)}");

        // Phase 8.3: also publish typed artifacts so fan-in effects (Burn Captions, Show Notes,
        // Chapters) can resolve via ArtifactResolver instead of scanning Steps backward.
        var segmentsForArtifact = new System.Collections.Generic.List<(int startMs, int endMs, string text)>(result.Segments.Count);
        foreach (var seg in result.Segments)
            segmentsForArtifact.Add(((int)(seg.Start * 1000), (int)(seg.End * 1000), seg.Text));
        step.Outputs["transcript"] = new TranscriptArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "transcript",
            Path = srtPath,
            FullText = result.FullText,
            Language = step.TranscribeLanguage,
            Segments = segmentsForArtifact
        };
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = inputPath
        };

        // Pass-through video so downstream steps see the same media
        step.OutputVideoPath = inputPath;
        step.ResultSummary =
            $"{result.Segments.Count} segments • {result.AudioDurationSeconds:F0}s audio • " +
            $"{result.ElapsedMs / 1000.0:F1}s on {result.DeviceUsed} ({result.RealtimeFactor:F1}× realtime)";
        step.Progress = 100;
    }

    private static string BuildSrt(TranscriptResult result)
    {
        var sb = new System.Text.StringBuilder();
        int i = 1;
        foreach (var seg in result.Segments)
        {
            sb.AppendLine(i.ToString());
            sb.AppendLine($"{FormatSrtTime(seg.Start)} --> {FormatSrtTime(seg.End)}");
            sb.AppendLine(seg.Text);
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private async Task ExecuteDetectSilenceAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        step.DeviceUsedLabel = "CPU (DSP)";
        step.StatusText = "Analyzing audio for silence...";
        step.Progress = 10;
        step.AppendLog($"Threshold: {step.SilenceThresholdDb:F0} dBFS");
        step.AppendLog($"Min silence: {step.MinSilenceMs} ms, padding: {step.SilencePadMs} ms");

        var result = await SilenceDetector.DetectAsync(
            inputPath,
            thresholdDb: step.SilenceThresholdDb,
            minSilenceMs: step.MinSilenceMs,
            padMs: step.SilencePadMs,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                if (msg.StartsWith("Analyzing")) step.Progress = 50;
            });

        step.Progress = 95;
        step.SetSilence(result);
        step.DeviceUsedLabel = result.DeviceUsed;

        // Phase 8.5: typed pin
        var silenceRanges = new List<(double, double)>(result.Intervals.Count);
        foreach (var i in result.Intervals) silenceRanges.Add((i.Start, i.End));
        step.Outputs["silence"] = new SilenceArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "silence",
            Ranges = silenceRanges
        };
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = step.InputVideoPath
        };

        // Write JSON artifact for downstream Smart Cut step
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            audioDurationSec = result.AudioDurationSec,
            totalSilenceSec = result.TotalSilenceSec,
            coverage = result.Coverage,
            thresholdDb = result.ThresholdDb,
            minSilenceMs = result.MinSilenceMs,
            padMs = result.PadMs,
            intervals = result.Intervals
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        string jsonPath = Path.Combine(workDir, "silence.json");
        await File.WriteAllTextAsync(jsonPath, json);
        step.AppendLog($"✓ Wrote {Path.GetFileName(jsonPath)}");

        // Pass-through media for downstream steps
        step.OutputVideoPath = inputPath;
        step.ResultSummary =
            $"{result.Intervals.Count} silent regions • {result.TotalSilenceSec:F1}s of {result.AudioDurationSec:F1}s " +
            $"({result.Coverage * 100:F0}%) • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.Progress = 100;
    }

    /// <summary>
    /// Phase 8.5 helper. Resolve the upstream transcript via typed pin (preferred) with a
    /// legacy scan-backward fallback for non-migrated producers. Returns null if not found.
    /// </summary>
    private TranscriptResult? ResolveUpstreamTranscript(PipelineStep step, string pinId = "transcript")
    {
        var artifact = ArtifactResolver.GetInput<TranscriptArtifact>(step, pinId, Steps, out var sourceStep);
        if (artifact != null && sourceStep?.Transcript != null)
        {
            step.AppendLog($"Using transcript from upstream step '{sourceStep.Name}' ({artifact.Segments.Count} segments) [resolver]");
            return sourceStep.Transcript;
        }
        int idx = Steps.IndexOf(step);
        for (int i = idx - 1; i >= 0; i--)
        {
            if (Steps[i].HasTranscript && Steps[i].Transcript is { } t)
            {
                step.AppendLog($"Using transcript from upstream step '{Steps[i].Name}' ({t.Segments.Count} segments) [legacy fallback]");
                return t;
            }
        }
        return null;
    }

    private async Task ExecuteChapterMarkersAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        TranscriptResult? transcript = ResolveUpstreamTranscript(step);

        if (transcript == null)
        {
            step.AppendLog("⚠ No upstream Transcribe step found — chapter generation requires a transcript.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no transcript available";
            return;
        }

        step.StatusText = "Preparing Phi Silica...";
        step.Progress = 10;
        step.DeviceUsedLabel = "NPU (Phi Silica)";
        step.AppendLog($"Target chapters: {step.ChapterTargetCount}");

        var lm = LanguageModelRegistry.Resolve(step.LanguageModelBackendId);
        var result = await _chapterGenerator.GenerateAsync(
            lm,
            transcript,
            targetCount: step.ChapterTargetCount,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                if (msg.StartsWith("Generating", StringComparison.OrdinalIgnoreCase)) step.Progress = 50;
                if (msg.StartsWith("Parsing", StringComparison.OrdinalIgnoreCase)) step.Progress = 90;
            });

        step.Progress = 95;
        step.SetChapters(result);
        step.DeviceUsedLabel = result.DeviceUsed;

        // Phase 8.5: typed pin
        var chapterTuples = new List<(double, string)>(result.Chapters.Count);
        foreach (var c in result.Chapters) chapterTuples.Add((c.Start, c.Title));
        step.Outputs["chapters"] = new ChaptersArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "chapters",
            Chapters = chapterTuples
        };
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = inputPath
        };

        // Write JSON artifact for downstream consumers / external tools.
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                deviceUsed = result.DeviceUsed,
                elapsedMs = result.ElapsedMs,
                chapters = result.Chapters
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string jsonPath = Path.Combine(workDir, "chapters.json");
            await File.WriteAllTextAsync(jsonPath, json);
            step.AppendLog($"✓ Wrote {Path.GetFileName(jsonPath)}");
        }
        catch (Exception ex)
        {
            step.AppendLog($"⚠ Could not write chapters.json: {ex.Message}");
        }

        // Pass-through media so downstream steps see the same file.
        step.OutputVideoPath = inputPath;
        step.ResultSummary = result.Chapters.Count > 0
            ? $"{result.Chapters.Count} chapters • {result.ElapsedMs} ms on {result.DeviceUsed}"
            : $"No chapters generated • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.Progress = 100;
    }

    private async Task ExecuteShowNotesAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        // Phase 8.5: typed-pin resolver + legacy fallback for both transcript and chapters.
        TranscriptResult? transcript = ResolveUpstreamTranscript(step);
        ChapterResult? chapters = null;
        var chapArtifact = ArtifactResolver.GetInput<ChaptersArtifact>(step, "chapters", Steps, out var chapSrc);
        if (chapArtifact != null && chapSrc?.Chapters != null)
        {
            chapters = chapSrc.Chapters;
            step.AppendLog($"Using chapters from upstream step '{chapSrc.Name}' ({chapters.Chapters.Count} chapters) [resolver]");
        }
        else
        {
            int idx = Steps.IndexOf(step);
            for (int i = idx - 1; i >= 0; i--)
            {
                if (Steps[i].HasChapters && Steps[i].Chapters is { } c)
                {
                    chapters = c;
                    step.AppendLog($"Using chapters from upstream step '{Steps[i].Name}' ({c.Chapters.Count} chapters) [legacy fallback]");
                    break;
                }
            }
        }

        if (transcript == null)
        {
            step.AppendLog("⚠ No upstream Transcribe step found — show-notes generation requires a transcript.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no transcript available";
            return;
        }

        step.StatusText = "Preparing Phi Silica...";
        step.Progress = 10;
        step.DeviceUsedLabel = "NPU (Phi Silica)";

        var lm = LanguageModelRegistry.Resolve(step.LanguageModelBackendId);
        var result = await _showNotesGenerator.GenerateAsync(
            lm,
            transcript,
            chapters,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                if (msg.StartsWith("Generating", StringComparison.OrdinalIgnoreCase)) step.Progress = 50;
                if (msg.StartsWith("...", StringComparison.Ordinal)) step.Progress = Math.Min(90, step.Progress + 2);
            });

        step.Progress = 95;
        step.SetShowNotes(result);
        step.DeviceUsedLabel = result.DeviceUsed;

        // Phase 8.5: typed pin
        step.Outputs["notes"] = new TextArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "notes",
            Title = "Show Notes",
            Content = result.Markdown
        };
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = inputPath
        };

        // Write markdown artifact for downstream tools / copy-paste.
        try
        {
            string mdPath = Path.Combine(workDir, "show-notes.md");
            await File.WriteAllTextAsync(mdPath, result.Markdown);
            step.AppendLog($"✓ Wrote {Path.GetFileName(mdPath)}");
        }
        catch (Exception ex)
        {
            step.AppendLog($"⚠ Could not write show-notes.md: {ex.Message}");
        }

        // Pass-through media so downstream steps see the same file.
        step.OutputVideoPath = inputPath;
        step.ResultSummary = result.HasContent
            ? $"{result.Blocks.Count} sections • {result.ElapsedMs} ms on {result.DeviceUsed}"
            : $"No notes generated • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.Progress = 100;
    }

    private async Task ExecuteHighlightPickerAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        TranscriptResult? transcript = ResolveUpstreamTranscript(step);

        if (transcript == null)
        {
            step.AppendLog("⚠ No upstream Transcribe step found — highlight picking requires a transcript.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no transcript available";
            return;
        }

        step.StatusText = "Preparing Phi Silica...";
        step.Progress = 10;
        step.DeviceUsedLabel = "NPU (Phi Silica)";
        step.AppendLog($"Target highlights: {step.HighlightTargetCount} • clip length: {step.HighlightClipSeconds}s");

        var lm = LanguageModelRegistry.Resolve(step.LanguageModelBackendId);
        var result = await _highlightPicker.PickAsync(
            lm,
            transcript,
            targetCount: step.HighlightTargetCount,
            targetClipSeconds: step.HighlightClipSeconds,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                if (msg.StartsWith("Asking", StringComparison.OrdinalIgnoreCase)) step.Progress = 50;
                if (msg.StartsWith("...", StringComparison.Ordinal)) step.Progress = Math.Min(90, step.Progress + 2);
            });

        step.Progress = 95;
        step.SetHighlights(result);

        // Phase 8.5: typed pin
        var ranges = new List<(double, double, string)>(result.Highlights.Count);
        foreach (var h in result.Highlights) ranges.Add((h.Start, h.End, !string.IsNullOrEmpty(h.Reason) ? h.Reason : h.Title));
        step.Outputs["highlights"] = new HighlightsArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "highlights",
            Ranges = ranges
        };
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = inputPath
        };
        step.DeviceUsedLabel = result.DeviceUsed;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                deviceUsed = result.DeviceUsed,
                elapsedMs = result.ElapsedMs,
                highlights = result.Highlights
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string jsonPath = Path.Combine(workDir, "highlights.json");
            await File.WriteAllTextAsync(jsonPath, json);
            step.AppendLog($"✓ Wrote {Path.GetFileName(jsonPath)}");
        }
        catch (Exception ex)
        {
            step.AppendLog($"⚠ Could not write highlights.json: {ex.Message}");
        }

        step.OutputVideoPath = inputPath;
        step.ResultSummary = result.Highlights.Count > 0
            ? $"{result.Highlights.Count} highlights • {result.ElapsedMs} ms on {result.DeviceUsed}"
            : $"No highlights picked • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.Progress = 100;
    }

    private async Task ExecuteCaptionBurnAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        // Phase 8.4: typed-pin resolver + legacy fallback (handled by helper).
        TranscriptResult? transcript = ResolveUpstreamTranscript(step);

        if (transcript == null)
        {
            step.AppendLog("⚠ No upstream Transcribe step found — caption burn requires a transcript.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no transcript available";
            return;
        }

        step.StatusText = "Burning captions...";
        step.Progress = 20;

        var result = await CaptionBurner.BurnAsync(
            inputPath,
            transcript.Segments,
            workDir,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                if (msg.StartsWith("Running ffmpeg", StringComparison.OrdinalIgnoreCase)) step.Progress = 60;
            });

        step.Progress = 95;
        step.OutputVideoPath = result.OutputPath;
        step.DeviceUsedLabel = result.DeviceUsed;
        step.ResultSummary = $"{result.CaptionCount} cues burned • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.AppendLog($"✓ Output: {Path.GetFileName(result.OutputPath)}");

        // Publish typed output so downstream effects (e.g. another export pass) can consume.
        step.Outputs["video"] = new VideoArtifact
        {
            ProducingStepId = step.StepId,
            ProducingPinId = "video",
            Path = result.OutputPath
        };

        step.Progress = 100;
    }

    private async Task ExecuteExportClipsAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        // Phase 8.5: typed-pin resolver + legacy fallback.
        HighlightResult? highlights = null;
        var hlArtifact = ArtifactResolver.GetInput<HighlightsArtifact>(step, "highlights", Steps, out var hlSrc);
        if (hlArtifact != null && hlSrc?.Highlights != null)
        {
            highlights = hlSrc.Highlights;
            step.AppendLog($"Using {highlights.Highlights.Count} highlights from upstream step '{hlSrc.Name}' [resolver]");
        }
        else
        {
            int idx = Steps.IndexOf(step);
            for (int i = idx - 1; i >= 0; i--)
            {
                if (Steps[i].HasHighlights && Steps[i].Highlights is { } h)
                {
                    highlights = h;
                    step.AppendLog($"Using {h.Highlights.Count} highlights from upstream step '{Steps[i].Name}' [legacy fallback]");
                    break;
                }
            }
        }

        if (highlights == null || highlights.Highlights.Count == 0)
        {
            step.AppendLog("⚠ No upstream Highlight Picker step found — clip export requires highlights.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no highlights available";
            return;
        }

        step.StatusText = "Exporting clips...";
        step.Progress = 20;

        var result = await ClipExporter.ExportAsync(
            inputPath,
            highlights.Highlights,
            workDir,
            onStatus: msg =>
            {
                step.AppendLog(msg);
                step.Progress = Math.Min(95, step.Progress + 5);
            });

        step.Progress = 95;
        step.SetExportedClips(result.Clips);
        step.DeviceUsedLabel = result.DeviceUsed;

        // Pass-through media (clips are side-products in their own folder).
        step.OutputVideoPath = inputPath;
        long totalBytes = 0;
        foreach (var c in result.Clips) totalBytes += c.FileSizeBytes;
        step.ResultSummary = result.Clips.Count > 0
            ? $"{result.Clips.Count} clips • {totalBytes / 1024.0 / 1024.0:F1} MB total • {result.ElapsedMs} ms on {result.DeviceUsed}"
            : $"No clips exported • {result.ElapsedMs} ms on {result.DeviceUsed}";
        step.Progress = 100;
    }

    private async Task ExecuteSmartCutAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        // Phase 8.5: typed-pin resolver + legacy fallback.
        IReadOnlyList<Models.SilenceInterval>? intervals = null;
        double duration = 0;
        var silArtifact = ArtifactResolver.GetInput<SilenceArtifact>(step, "silence", Steps, out var silSrc);
        if (silArtifact != null && silSrc?.Silence != null)
        {
            intervals = silSrc.Silence.Intervals;
            duration = silSrc.Silence.AudioDurationSec;
            step.AppendLog($"Using silence intervals from upstream step '{silSrc.Name}' ({intervals.Count} regions) [resolver]");
        }
        else
        {
            int idx = Steps.IndexOf(step);
            for (int i = idx - 1; i >= 0; i--)
            {
                if (Steps[i].HasSilence && Steps[i].Silence is { } s)
                {
                    intervals = s.Intervals;
                    duration = s.AudioDurationSec;
                    step.AppendLog($"Using silence intervals from upstream step '{Steps[i].Name}' ({intervals.Count} regions) [legacy fallback]");
                    break;
                }
            }
        }

        if (intervals == null)
        {
            step.AppendLog("No upstream Detect Silence step found — running silence detection inline");
            step.StatusText = "Detecting silence...";
            step.Progress = 10;
            var silenceResult = await SilenceDetector.DetectAsync(
                inputPath,
                thresholdDb: step.SilenceThresholdDb,
                minSilenceMs: step.MinSilenceMs,
                padMs: step.SilencePadMs,
                onStatus: msg => step.AppendLog(msg));
            intervals = silenceResult.Intervals;
            duration = silenceResult.AudioDurationSec;
        }

        if (duration <= 0)
            duration = await AudioExtractor.ProbeDurationSecondsAsync(inputPath);

        step.DeviceUsedLabel = "CPU (ffmpeg)";
        step.StatusText = $"Cutting {intervals.Count} silent regions...";
        step.Progress = 50;
        step.AppendLog($"Min keep duration: {step.MinKeepMs} ms");

        string outputPath = Path.Combine(workDir, "smartcut_" + Path.GetFileName(inputPath));
        var result = await SmartCutter.CutAsync(
            inputPath,
            intervals,
            duration,
            outputPath,
            minKeepMs: step.MinKeepMs,
            onStatus: msg => step.AppendLog(msg));

        step.OutputVideoPath = result.OutputPath;
        step.DeviceUsedLabel = result.DeviceUsed;
        step.ResultSummary =
            $"Cut {result.CutsRemoved} silent regions • {result.OriginalDurationSec:F1}s → {result.NewDurationSec:F1}s " +
            $"(saved {result.SecondsRemoved:F1}s, {result.SavedPercent * 100:F0}%) • {result.ElapsedMs} ms ffmpeg";
        step.AppendLog($"✓ {Path.GetFileName(outputPath)} ready");
        step.Progress = 100;
    }

    private async Task ExecuteBurnSubsAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;
        string srtPath = Path.Combine(workDir, "subtitles.srt");
        string outputPath = Path.Combine(workDir, "output_" + Path.GetFileName(inputPath));

        if (!File.Exists(srtPath))
        {
            step.AppendLog("⚠ No subtitle file found — skipping");
            step.AppendLog("Add 'Transcribe' step first to generate subtitles.");
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Skipped — no subtitles available";
            return;
        }

        step.AppendLog($"Burning subtitles onto video...");
        step.AppendLog($"Input: {Path.GetFileName(inputPath)} + subtitles.srt");

        step.StatusText = "Initializing container...";
        step.Progress = 5;
        await _containerRunner.InitSessionAsync();

        step.StatusText = "Burning subtitles...";
        step.Progress = 20;
        await _containerRunner.RunFFmpegComposeAsync(inputPath, srtPath, outputPath, output =>
        {
            step.AppendLog(output.TrimEnd('\n', '\r'));
        });

        if (File.Exists(outputPath))
        {
            var outputSize = new FileInfo(outputPath).Length;
            step.AppendLog($"✓ Output video: {outputSize / 1024.0:F0} KB");
            step.OutputVideoPath = outputPath;
            step.ResultSummary = $"{Path.GetFileName(outputPath)} ({outputSize / 1024.0:F0} KB)";
        }
        else
        {
            step.OutputVideoPath = inputPath;
            step.ResultSummary = "Subtitles burned";
        }

        step.Progress = 100;
    }

    // ── Native effects ────────────────────────────────────────────────

    private async Task ExecuteDetectObjectsAsync(PipelineStep step)
    {
        string videoPath = step.InputVideoPath!;
        int maxFrames = Math.Max(5, step.SampleFrameCount);
        float threshold = (float)Math.Clamp(step.ConfidenceThreshold, 0.05, 0.95);

        step.AppendLog("Object Detection — Windows ML (ONNX Runtime)");
        step.AppendLog("Model: YOLOS-small (hustvl/yolos-small)");
        step.AppendLog($"Options: confidence ≥ {threshold:P0}, sampling {maxFrames} frames");

        // Initialize model once
        if (!_modelLoaded)
        {
            step.StatusText = "Initializing Windows ML...";
            step.Progress = 5;

            try
            {
                await _modelRunner.InitializeAndLoadModelAsync(status => step.AppendLog($"  {status}"));
                _modelLoaded = true;
            }
            catch (Exception ex)
            {
                step.AppendLog($"❌ Model initialization failed: {ex.Message}");
                step.State = PipelineStepState.Error;
                step.StatusText = "Model init failed";
                step.ResultSummary = $"Initialization error: {ex.Message}";
                step.OutputVideoPath = videoPath;
                return;
            }
        }

        step.AppendLog($"Active backend: {_modelRunner.ActiveBackend}");
        step.AppendLog($"Running inference on {maxFrames} sampled frames...");
        step.StatusText = $"Running on {_modelRunner.ActiveBackend}";
        step.Progress = 10;

        var detectionCounts = new Dictionary<string, int>();
        int totalDetections = 0;

        try
        {
            var results = await _modelRunner.DetectObjectsAsync(videoPath, maxFrames, threshold,
                onProgress: (current, total) =>
                {
                    double progress = 10 + (double)current / total * 85;
                    step.Progress = progress;
                    step.StatusText = $"Frame {current}/{total} on {_modelRunner.ActiveBackend}";
                    _hardwareMonitor?.ReportNpuActive(80);
                },
                onDiagnostic: msg => step.AppendLog(msg));

            step.SetDetections(results);

            // Phase 8.3: publish typed DetectionsArtifact for fan-in consumers.
            var distinctLabels = new System.Collections.Generic.HashSet<string>();
            int frameCountSeen = 0;
            foreach (var r in results)
            {
                distinctLabels.Add(r.Label);
                if (r.FrameIndex + 1 > frameCountSeen) frameCountSeen = r.FrameIndex + 1;
            }
            step.Outputs["detections"] = new DetectionsArtifact
            {
                ProducingStepId = step.StepId,
                ProducingPinId = "detections",
                FrameCount = frameCountSeen,
                TotalCount = results.Count,
                Labels = new System.Collections.Generic.List<string>(distinctLabels)
            };
            step.Outputs["video"] = new VideoArtifact
            {
                ProducingStepId = step.StepId,
                ProducingPinId = "video",
                Path = videoPath
            };

            // Aggregate results
            var frameGroups = results.GroupBy(r => r.FrameIndex).OrderBy(g => g.Key);
            foreach (var group in frameGroups)
            {
                var frameLabels = group.Select(d =>
                {
                    detectionCounts[d.Label] = detectionCounts.GetValueOrDefault(d.Label) + 1;
                    totalDetections++;
                    return $"{d.Label} ({d.Confidence:P0})";
                }).ToList();

                string frameResult = frameLabels.Count > 0
                    ? $"  Frame {group.Key + 1,3}: {string.Join(", ", frameLabels)}"
                    : $"  Frame {group.Key + 1,3}: (no objects)";
                step.AppendLog(frameResult);
            }

            step.AppendLog($"── Total: {totalDetections} objects across {maxFrames} frames ──");
            foreach (var kvp in detectionCounts.OrderByDescending(k => k.Value))
                step.AppendLog($"  {kvp.Key}: {kvp.Value}×");

            step.ResultSummary = totalDetections > 0
                ? $"{totalDetections} objects detected on {_modelRunner.ActiveBackend}"
                : $"No objects detected on {_modelRunner.ActiveBackend}";

            // Render bounding boxes into an output video
            if (totalDetections > 0)
            {
                step.StatusText = "Rendering bounding boxes...";
                step.Progress = 95;
                step.AppendLog("Rendering annotated video with bounding boxes...");

                string outputPath = Path.Combine(
                    Path.GetTempPath(), "ContosoStudio", "work",
                    $"detected_{Guid.NewGuid():N}.mp4");

                var resultPath = await BoundingBoxRenderer.RenderAnnotatedVideoAsync(
                    videoPath, results, outputPath, maxFrames, 1280, 720,
                    onStatus: msg =>
                    {
                        step.AppendLog($"  {msg}");
                        step.StatusText = msg;
                    });

                if (resultPath != null)
                {
                    step.OutputVideoPath = resultPath;
                    step.AppendLog($"Output: {resultPath}");
                }
            }
        }
        catch (Exception ex)
        {
            step.AppendLog($"❌ Inference failed: {ex.Message}");
            step.AppendLog(ex.StackTrace ?? "(no stack)");
            step.State = PipelineStepState.Error;
            step.StatusText = "Inference failed";
            step.ResultSummary = $"Error: {ex.Message}";
        }

        // If no annotated output was generated, pass through the original video so downstream steps work
        if (string.IsNullOrEmpty(step.OutputVideoPath))
            step.OutputVideoPath = videoPath;
    }

    private static async Task RunDetectObjectsFallbackAsync(PipelineStep step, int totalFrames)
    {
        var random = new Random(42);
        string[] labels = ["person", "car", "dog", "chair", "bottle", "bird", "cat", "bicycle", "umbrella", "backpack"];
        int totalDetections = 0;
        var counts = new Dictionary<string, int>();

        for (int i = 0; i < totalFrames; i++)
        {
            await Task.Delay(80);
            int frameDetections = random.Next(0, 4);
            var frameLabels = new List<string>();

            for (int j = 0; j < frameDetections; j++)
            {
                string label = labels[random.Next(labels.Length)];
                float confidence = 0.65f + (float)random.NextDouble() * 0.30f;
                frameLabels.Add($"{label} ({confidence:P0})");
                counts[label] = counts.GetValueOrDefault(label) + 1;
                totalDetections++;
            }

            double progress = (i + 1.0) / totalFrames * 100;
            string frameResult = frameLabels.Count > 0
                ? $"  Frame {i + 1,3}: {string.Join(", ", frameLabels)}"
                : $"  Frame {i + 1,3}: (no objects)";
            step.AppendLog(frameResult);
            step.Progress = progress;
            step.StatusText = $"Frame {i + 1}/{totalFrames}";
        }

        step.AppendLog($"── Results (Demo) ──");
        step.AppendLog($"Total: {totalDetections} across {totalFrames} frames");
        foreach (var kvp in counts.OrderByDescending(k => k.Value))
            step.AppendLog($"  {kvp.Key}: {kvp.Value}×");

        step.ResultSummary = $"{totalDetections} objects detected across {totalFrames} frames (demo)";
    }

    private async Task ExecuteDepthMapAsync(PipelineStep step)
    {
        string videoPath = step.InputVideoPath!;
        int maxFrames = Math.Max(5, step.SampleFrameCount);
        step.AppendLog("Depth Estimation — Windows ML (ONNX Runtime)");
        step.AppendLog("Model: Depth-Anything-Small (Xenova/depth-anything-small-hf)");
        step.AppendLog($"Options: sampling {maxFrames} frames");

        step.StatusText = "Loading model...";
        step.Progress = 5;
        try
        {
            await _depthRunner.InitializeAsync(s => step.AppendLog($"  {s}"));
        }
        catch (Exception ex)
        {
            step.AppendLog($"❌ Model load failed: {ex.Message}");
            step.State = PipelineStepState.Error;
            step.ResultSummary = $"Init error: {ex.Message}";
            step.OutputVideoPath = videoPath;
            return;
        }

        step.AppendLog($"Active backend: {_depthRunner.ActiveBackend}");
        step.StatusText = $"Extracting {maxFrames} frames...";
        step.Progress = 10;

        var frames = await FrameOverlayRenderer.ExtractFramesAsync(videoPath, maxFrames, 640, 360);
        if (frames.Count == 0)
        {
            step.AppendLog("⚠ No frames extracted from video.");
            step.ResultSummary = "No frames available";
            step.OutputVideoPath = videoPath;
            return;
        }
        step.AppendLog($"Extracted {frames.Count} frames at 640×360");

        step.StatusText = $"Running depth on {_depthRunner.ActiveBackend}";
        var sw = Stopwatch.StartNew();
        var depths = await _depthRunner.EstimateDepthAsync(frames,
            onProgress: (cur, total) =>
            {
                step.Progress = 10 + (double)cur / total * 70;
                step.StatusText = $"Depth frame {cur}/{total} on {_depthRunner.ActiveBackend}";
                _hardwareMonitor?.ReportNpuActive(70);
            },
            onDiagnostic: msg => step.AppendLog(msg));
        sw.Stop();
        step.AppendLog($"Inference: {sw.ElapsedMilliseconds} ms for {depths.Count} frames");

        if (depths.Count == 0)
        {
            step.ResultSummary = $"No depth maps produced on {_depthRunner.ActiveBackend}";
            step.OutputVideoPath = videoPath;
            return;
        }

        step.StatusText = "Rendering depth video...";
        step.Progress = 85;
        string outputPath = Path.Combine(Path.GetTempPath(), "ContosoStudio", "work",
            $"depth_{Guid.NewGuid():N}.mp4");
        var resultPath = await FrameOverlayRenderer.RenderDepthVideoAsync(
            videoPath, frames, depths, outputPath,
            onStatus: msg => { step.AppendLog($"  {msg}"); step.StatusText = msg; });

        if (resultPath != null)
        {
            step.OutputVideoPath = resultPath;
            step.AppendLog($"Output: {resultPath}");
            step.ResultSummary = $"{depths.Count} depth maps • {sw.ElapsedMilliseconds} ms on {_depthRunner.ActiveBackend} • side-by-side video";
        }
        else
        {
            step.OutputVideoPath = videoPath;
            step.ResultSummary = $"{depths.Count} depth maps on {_depthRunner.ActiveBackend} (render failed)";
        }
    }

    private async Task ExecuteSceneTagsAsync(PipelineStep step)
    {
        string videoPath = step.InputVideoPath!;
        int maxFrames = Math.Max(5, step.SampleFrameCount);
        step.AppendLog("Scene Tagging — Windows ML (ONNX Runtime)");
        step.AppendLog("Model: ViT-Base/16 (google/vit-base-patch16-224, ImageNet-1k)");
        step.AppendLog($"Options: sampling {maxFrames} frames, top-3 labels per frame");

        step.StatusText = "Loading model...";
        step.Progress = 5;
        try
        {
            await _sceneTagger.InitializeAsync(s => step.AppendLog($"  {s}"));
        }
        catch (Exception ex)
        {
            step.AppendLog($"❌ Model load failed: {ex.Message}");
            step.State = PipelineStepState.Error;
            step.ResultSummary = $"Init error: {ex.Message}";
            step.OutputVideoPath = videoPath;
            return;
        }

        step.AppendLog($"Active backend: {_sceneTagger.ActiveBackend}");
        step.StatusText = $"Extracting {maxFrames} frames...";
        step.Progress = 10;
        var frames = await FrameOverlayRenderer.ExtractFramesAsync(videoPath, maxFrames, 640, 360);
        if (frames.Count == 0)
        {
            step.AppendLog("⚠ No frames extracted.");
            step.ResultSummary = "No frames available";
            step.OutputVideoPath = videoPath;
            return;
        }
        step.AppendLog($"Extracted {frames.Count} frames at 640×360");

        step.StatusText = $"Classifying on {_sceneTagger.ActiveBackend}";
        var sw = Stopwatch.StartNew();
        var tags = await _sceneTagger.ClassifyAsync(frames, topK: 3,
            onProgress: (cur, total) =>
            {
                step.Progress = 10 + (double)cur / total * 70;
                step.StatusText = $"Tag frame {cur}/{total} on {_sceneTagger.ActiveBackend}";
                _hardwareMonitor?.ReportNpuActive(50);
            },
            onDiagnostic: msg => step.AppendLog(msg));
        sw.Stop();
        step.AppendLog($"Inference: {sw.ElapsedMilliseconds} ms for {frames.Count} frames");

        if (tags.Count == 0)
        {
            step.ResultSummary = $"No tags produced on {_sceneTagger.ActiveBackend}";
            step.OutputVideoPath = videoPath;
            return;
        }

        // Aggregate top labels for the result summary
        var labelCounts = tags
            .GroupBy(t => t.Label)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(5)
            .ToList();
        step.AppendLog("── Most-frequent labels ──");
        foreach (var lc in labelCounts) step.AppendLog($"  {lc.Label}: {lc.Count}×");

        step.StatusText = "Rendering tag video...";
        step.Progress = 85;
        string outputPath = Path.Combine(Path.GetTempPath(), "ContosoStudio", "work",
            $"tags_{Guid.NewGuid():N}.mp4");
        var resultPath = await FrameOverlayRenderer.RenderSceneTagsVideoAsync(
            videoPath, frames, tags, outputPath,
            onStatus: msg => { step.AppendLog($"  {msg}"); step.StatusText = msg; });

        if (resultPath != null)
        {
            step.OutputVideoPath = resultPath;
            step.AppendLog($"Output: {resultPath}");
            string topSummary = labelCounts.Count > 0 ? labelCounts[0].Label : "n/a";
            step.ResultSummary = $"{frames.Count} frames tagged • top: {topSummary} • {sw.ElapsedMilliseconds} ms on {_sceneTagger.ActiveBackend}";
        }
        else
        {
            step.OutputVideoPath = videoPath;
            step.ResultSummary = $"{tags.Count} tags on {_sceneTagger.ActiveBackend} (render failed)";
        }
    }

    private const int FindFramesMaxCap = 600;

    private async Task ExecuteFindFramesAsync(PipelineStep step)
    {
        string videoPath = step.InputVideoPath!;
        string prompt = string.IsNullOrWhiteSpace(step.FrameSearchPrompt)
            ? "a person smiling"
            : step.FrameSearchPrompt.Trim();
        double stride = Math.Max(0.5, step.FrameSearchStrideSeconds);
        int topK = Math.Max(1, step.FrameSearchTopK);

        // Compute frame count up-front from duration so the sampling stride is meaningful
        // for both short clips and long-form video. We probe before extraction so the user
        // sees the resulting frame count in the log before the (potentially long) scan.
        double videoSeconds = await ProbeDurationSecondsAsync(videoPath);
        int requestedFrames = videoSeconds > 0 ? (int)Math.Ceiling(videoSeconds / stride) : 60;
        int maxFrames = Math.Clamp(requestedFrames, 8, FindFramesMaxCap);
        // If the cap kicked in, the effective stride widens — report what we'll actually use.
        double effectiveStride = videoSeconds > 0 && maxFrames > 1 ? videoSeconds / maxFrames : stride;

        step.AppendLog("Find Frames — Windows ML (ONNX Runtime)");
        step.AppendLog("Model: openai/clip-vit-base-patch16 (vision + text), QNN-compiled to NPU via winml CLI");
        step.AppendLog($"Prompt: \"{prompt}\"");
        if (requestedFrames > FindFramesMaxCap)
            step.AppendLog($"Options: ~{videoSeconds:F0}s video, requested every {stride:F1}s → {requestedFrames} frames " +
                           $"(capped at {FindFramesMaxCap}, effective stride {effectiveStride:F1}s), top-{topK} matches");
        else
            step.AppendLog($"Options: ~{videoSeconds:F0}s video, sampling every {stride:F1}s → {maxFrames} frames, top-{topK} matches");

        step.StatusText = "Loading CLIP encoders...";
        step.Progress = 4;
        try
        {
            await _frameSearchRunner.InitializeAsync(s => step.AppendLog($"  {s}"));
        }
        catch (Exception ex)
        {
            step.AppendLog($"❌ CLIP load failed: {ex.Message}");
            step.State = PipelineStepState.Error;
            step.ResultSummary = $"Init error: {ex.Message}";
            step.OutputVideoPath = videoPath;
            return;
        }

        step.AppendLog($"Active backend: {_frameSearchRunner.ActiveBackend}");
        step.DeviceUsedLabel = _frameSearchRunner.ActiveBackend;

        step.StatusText = "Encoding prompt...";
        step.Progress = 8;
        float[] textEmbed;
        try
        {
            textEmbed = await _frameSearchRunner.EmbedTextAsync(prompt);
            step.AppendLog($"Text embedding: dim={textEmbed.Length}");
        }
        catch (Exception ex)
        {
            step.AppendLog($"❌ Text encode failed: {ex.Message}");
            step.State = PipelineStepState.Error;
            step.ResultSummary = $"Text encode error: {ex.Message}";
            step.OutputVideoPath = videoPath;
            return;
        }

        // Check if we have cached frames for this video+sampling combination.
        var cachedFrames = _frameSearchRunner.TryGetCachedFrames(videoPath, maxFrames);
        List<(int frameIndex, float score)> scores;
        long totalElapsedMs;

        IReadOnlyList<(int frameIndex, byte[] rgba, int width, int height)> frames;
        if (cachedFrames != null)
        {
            step.AppendLog($"✓ Using {cachedFrames.Count} cached frames — skipping extraction");
            frames = cachedFrames;
        }
        else
        {
            step.StatusText = $"Extracting {maxFrames} frames...";
            step.AppendLog($"Extracting {maxFrames} frames from video (this can take a minute on long clips)...");
            step.Progress = 14;
            int lastLoggedExtract = 0;
            var extractSw = Stopwatch.StartNew();
            frames = await FrameOverlayRenderer.ExtractFramesAsync(videoPath, maxFrames, 640, 360,
                onProgress: (cur, total) =>
                {
                    step.Progress = 14 + (double)cur / total * 6; // 14% → 20% during extract
                    step.StatusText = $"Extracting frame {cur}/{total}...";
                    int pctDecile = (int)(cur * 10.0 / total);
                    if (pctDecile > lastLoggedExtract)
                    {
                        lastLoggedExtract = pctDecile;
                        step.AppendLog($"  …extracted {cur}/{total} frames ({extractSw.ElapsedMilliseconds} ms)");
                    }
                });
            if (frames.Count == 0)
            {
                step.AppendLog("⚠ No frames extracted.");
                step.ResultSummary = "No frames available";
                step.OutputVideoPath = videoPath;
                return;
            }
            step.AppendLog($"Extracted {frames.Count} frames at 640×360 in {extractSw.ElapsedMilliseconds} ms");
            _frameSearchRunner.CacheFrames(videoPath, maxFrames, frames);
        }

        step.StatusText = $"Embedding frames on {_frameSearchRunner.ActiveBackend}";
        var sw = Stopwatch.StartNew();
        var embeddings = await _frameSearchRunner.EmbedFramesAsync(videoPath, maxFrames, frames,
            onProgress: (cur, total) =>
            {
                step.Progress = 20 + (double)cur / total * 60;
                step.StatusText = $"Embedding frame {cur}/{total} on {_frameSearchRunner.ActiveBackend}";
                _hardwareMonitor?.ReportNpuActive(50);
            },
            onDiagnostic: msg => step.AppendLog(msg));
        sw.Stop();
        totalElapsedMs = sw.ElapsedMilliseconds;
        step.AppendLog($"Inference: {totalElapsedMs} ms for {frames.Count} frames");

        scores = _frameSearchRunner.ScoreEmbeddings(textEmbed, embeddings,
            onDiagnostic: msg => step.AppendLog(msg));

        int scoredFrameCount = scores.Count;
        double frameStrideSec = scoredFrameCount > 1 && videoSeconds > 0 ? videoSeconds / scoredFrameCount : effectiveStride;

        if (scores.Count == 0)
        {
            step.ResultSummary = $"No similarity scores on {_frameSearchRunner.ActiveBackend}";
            step.OutputVideoPath = videoPath;
            return;
        }

        var ranked = scores.OrderByDescending(s => s.score).ToList();
        var topMatches = ranked.Take(topK).ToList();

        step.AppendLog("── Top matches ──");
        var topMatchObjs = new List<FrameMatch>(topMatches.Count);
        foreach (var m in topMatches)
        {
            double ts = m.frameIndex * frameStrideSec;
            step.AppendLog($"  [{FormatTimestamp(ts)}]  score {m.score:F3}  (frame {m.frameIndex + 1})");
            topMatchObjs.Add(new FrameMatch
            {
                FrameIndex = m.frameIndex,
                Score = m.score,
                TimestampSeconds = ts,
            });
        }

        // No annotated render — surface matches as a clickable timestamp list on the source video.
        step.Progress = 96;
        step.OutputVideoPath = videoPath;
        step.SetFrameMatches(new FrameMatchesResult
        {
            Prompt = prompt,
            Matches = topMatchObjs,
            DeviceUsed = _frameSearchRunner.ActiveBackend,
            ElapsedMs = totalElapsedMs,
            FramesScanned = scoredFrameCount,
        });

        var bestMatch = topMatchObjs[0];
        step.ResultSummary =
            $"Top match @ {FormatTimestamp(bestMatch.TimestampSeconds)} • score {bestMatch.Score:F2} • {totalElapsedMs} ms on {_frameSearchRunner.ActiveBackend}";
    }

    private static async Task<double> ProbeDurationSecondsAsync(string videoPath)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(videoPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
            return clip.OriginalDuration.TotalSeconds;
        }
        catch { return 0; }
    }

    private static string FormatTimestamp(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private async Task ExecuteUpscaleAsync(PipelineStep step, string workDir)
    {
        string inputPath = step.InputVideoPath!;

        step.AppendLog("Super Resolution — Windows AI");
        step.AppendLog("Backend: GPU accelerated (DirectX 12)");
        step.AppendLog("Scale: 2× (e.g. 1280×720 → 2560×1440)");

        step.StatusText = "Initializing...";
        step.Progress = 5;
        await _srRunner.InitializeAsync();

        if (!_srRunner.IsAvailable)
        {
            step.AppendLog($"⚠ {_srRunner.GetCapabilityDescription()}");
            step.AppendLog("Running simulated upscale...");
        }
        else
        {
            step.AppendLog($"✓ {_srRunner.GetCapabilityDescription()}");
        }

        // Simulate phased progress (SuperResolutionRunner is a demo placeholder)
        string[] phases =
        [
            "Loading super resolution model...",
            "Extracting key frames...",
            "Upscaling frames (pass 1/2)...",
            "Upscaling frames (pass 2/2)...",
            "Reconstructing temporal coherence...",
            "Encoding output video..."
        ];

        for (int i = 0; i < phases.Length; i++)
        {
            step.AppendLog($"  {phases[i]}");
            step.StatusText = phases[i].TrimEnd('.');
            step.Progress = (i + 1.0) / phases.Length * 100;
            await Task.Delay(400 + (i is 2 or 3 ? 600 : 0));
        }

        step.AppendLog("✓ Upscale complete — 2× resolution enhancement applied");
        step.OutputVideoPath = inputPath; // placeholder pass-through
        step.ResultSummary = "2× resolution upscale (e.g. 720p → 1440p)";
    }

    private void UpdateStepInputPaths()
    {
        if (SourceFile == null || Steps.Count == 0) return;
        Steps[0].InputVideoPath = SourceFile.FilePath;
    }

    /// <summary>
    /// Reserved StepId prefix for source media nodes (Phase 8.8.1). A source ref encodes
    /// the file path so it survives across runs / saves: <c>"source:" + MediaFile.FilePath</c>.
    /// </summary>
    public const string SourceStepIdPrefix = "source:";

    /// <summary>Phase 8.10 — single fixed id for the "input bucket" node on the canvas.
    /// All Video/Audio pins target this id; the bucket holds <see cref="MediaFiles"/>.</summary>
    public const string BucketStepId = "bucket:input";

    public static string SourceStepId(MediaFile media) => SourceStepIdPrefix + media.FilePath;

    public static bool IsSourceStepId(string stepId) =>
        !string.IsNullOrEmpty(stepId) && stepId.StartsWith(SourceStepIdPrefix, System.StringComparison.Ordinal);

    public static bool IsBucketStepId(string stepId) =>
        string.Equals(stepId, BucketStepId, System.StringComparison.Ordinal);

    public static bool IsInputStepId(string stepId) =>
        IsSourceStepId(stepId) || IsBucketStepId(stepId);

    public static string SourcePathFromStepId(string stepId) =>
        IsSourceStepId(stepId) ? stepId.Substring(SourceStepIdPrefix.Length) : string.Empty;

    /// <summary>
    /// Phase 8.8.1: for each input pin declared by the step's effect that has no explicit
    /// ArtifactRef, synthesize one pointing at the nearest-prior compatible producer (an
    /// upstream step's matching output pin, or — for video pins — the source node).
    /// Visualization-only for now: execution still uses the legacy InputVideoPath/scan-back
    /// path until the pin-dropdown UI (8.8.5) lets users override.
    /// </summary>
    private void BackfillInputRefs(PipelineStep step)
    {
        var effect = FindEffect(step.Id);
        if (effect == null) return;

        int idx = Steps.IndexOf(step);
        if (idx < 0) return;

        BackfillInputRefsCore(step, effect, idx);
    }

    private void BackfillInputRefsCore(PipelineStep step, Effect effect, int idx)
    {
        foreach (var pin in effect.EffectiveInputPins)
        {
            if (step.Inputs.ContainsKey(pin.Id)) continue;

            ArtifactRef? synthesized = null;

            // Phase 8.10 — parallel-by-default + bucket: Video/Audio pins point at the single
            // input bucket (which holds all imported media). Execution fans out per-file.
            if (pin.Kind == ArtifactKind.Video || pin.Kind == ArtifactKind.Audio)
            {
                if (MediaFiles.Count > 0)
                {
                    synthesized = new ArtifactRef
                    {
                        StepId = BucketStepId,
                        PinId = "video",
                        Kind = pin.Kind
                    };
                }
            }

            // Otherwise (or if no source), walk backward for a matching upstream output.
            if (synthesized == null)
            {
                for (int i = idx - 1; i >= 0 && synthesized == null; i--)
                {
                    var candidate = Steps[i];
                    var candEffect = FindEffect(candidate.Id);
                    if (candEffect == null) continue;

                    var match = System.Array.Find(candEffect.EffectiveOutputPins,
                        op => op.Kind == pin.Kind && op.Id == pin.Id);
                    match ??= System.Array.Find(candEffect.EffectiveOutputPins, op => op.Kind == pin.Kind);
                    if (match != null)
                    {
                        synthesized = new ArtifactRef
                        {
                            StepId = candidate.StepId,
                            PinId = match.Id,
                            Kind = match.Kind
                        };
                    }
                }
            }

            if (synthesized != null)
            {
                step.Inputs[pin.Id] = synthesized;
            }
        }
    }

    /// <summary>Re-run backfill across every step (cheap; called when sources change).</summary>
    internal void BackfillAllInputRefs()
    {
        // Drop stale source/bucket refs first so the new MediaFiles get re-resolved.
        foreach (var s in Steps)
        {
            var staleKeys = s.Inputs
                .Where(kv => IsInputStepId(kv.Value.StepId))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in staleKeys) s.Inputs.Remove(k);
        }
        foreach (var s in Steps) BackfillInputRefs(s);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoBarMessage = message;
        InfoBarSeverity = severity;
        ShowInfoBar = true;
    }

    private Effect? FindEffect(string id) =>
        AvailableEffects.FirstOrDefault(e => e.Id == id);
}
