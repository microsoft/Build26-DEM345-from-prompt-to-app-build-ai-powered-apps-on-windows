using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using VideoStudio.Models;

namespace VideoStudio.Controls;

public sealed partial class PipelineStepCard : UserControl
{
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(PipelineStep), typeof(PipelineStepCard),
            new PropertyMetadata(null, OnStepChanged));

    public PipelineStep? Step
    {
        get => (PipelineStep?)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public event EventHandler? RunStepRequested;
    public event EventHandler? RemoveRequested;
    public event EventHandler? MoveUpRequested;
    public event EventHandler? MoveDownRequested;

    /// <summary>
    /// Callback to enumerate producers compatible with a given input pin on this step.
    /// Each candidate exposes (producerStepId, producerName, producerPinId, kind).
    /// Called when an empty input chip is clicked. Set by MainWindow during card creation.
    /// </summary>
    public Func<EffectPin, System.Collections.Generic.IReadOnlyList<PinCandidate>>? GetPinCandidates { get; set; }

    /// <summary>Called when the user picks a producer or clears a wire on this step.</summary>
    public Action<string /*pinId*/, ArtifactRef? /*newRef or null to clear*/>? PinSelectionChanged { get; set; }

    public sealed record PinCandidate(string ProducerStepId, string ProducerName, string ProducerPinId, ArtifactKind Kind);

    /// <summary>Static accessor for the effect catalog. Set once on MainWindow startup.</summary>
    public static Func<System.Collections.Generic.IReadOnlyList<Effect>> GetAllEffects { get; set; }
        = () => System.Array.Empty<Effect>();

    public PipelineStepCard()
    {
        this.InitializeComponent();
    }

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PipelineStepCard card) return;

        if (e.OldValue is PipelineStep oldStep)
            oldStep.PropertyChanged -= card.OnStepPropertyChanged;

        if (e.NewValue is PipelineStep newStep)
        {
            newStep.PropertyChanged += card.OnStepPropertyChanged;
            if (card.IsLoaded)
                card.UpdateAll(newStep);
            else
                card.Loaded += OnceLoaded;

            void OnceLoaded(object s, RoutedEventArgs args)
            {
                card.Loaded -= OnceLoaded;
                card.UpdateAll(newStep);
            }
        }
        else if (card.IsLoaded)
        {
            card.ClearAll();
        }
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PropertyChanged may fire from a background thread (e.g., onProgress callback inside Task.Run).
        // All UI access — including reading the Step DependencyProperty — must marshal to the UI thread.
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyStepPropertyChange(e);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => ApplyStepPropertyChange(e));
        }
    }

    private void ApplyStepPropertyChange(PropertyChangedEventArgs e)
    {
        if (Step is not { } step) return;

        switch (e.PropertyName)
        {
            case nameof(PipelineStep.Name):
                StepNameText.Text = step.Name;
                break;
            case nameof(PipelineStep.Description):
                DescriptionText.Text = step.Description;
                break;
            case nameof(PipelineStep.State):
            case nameof(PipelineStep.StateIcon):
            case nameof(PipelineStep.IsRunning):
                StateIconElement.Glyph = step.StateIcon;
                RunningRing.Visibility = step.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                UpdateVisualState(step);
                UpdateArtifactsToggleVisibility(step);
                UpdateDeviceBadge(step);
                if (step.IsDone) ArtifactsExpander.IsExpanded = true; // auto-open on completion
                break;
            case nameof(PipelineStep.EngineBadge):
                EngineBadgeText.Text = step.EngineBadge;
                break;
            case nameof(PipelineStep.DurationText):
                DurationTextBlock.Text = step.DurationText;
                break;
            case nameof(PipelineStep.ResultSummary):
            case nameof(PipelineStep.HasResult):
                ResultSummaryText.Text = step.ResultSummary;
                ResultSummaryText.Visibility = step.HasResult ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(PipelineStep.LogOutput):
                LogText.Text = step.LogOutput;
                UpdateArtifactsToggleVisibility(step);
                // Auto-scroll to the bottom while running
                LogScrollViewer?.ChangeView(null, double.MaxValue, null, disableAnimation: true);
                break;
            case nameof(PipelineStep.OutputVideoPath):
            case nameof(PipelineStep.HasOutput):
                UpdatePreviewPane(step);
                UpdateFilesPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.InputVideoPath):
                UpdateFilesPane(step);
                break;
            case nameof(PipelineStep.Detections):
            case nameof(PipelineStep.HasDetections):
            case nameof(PipelineStep.DetectionCount):
                UpdateDetectionsPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.Transcript):
            case nameof(PipelineStep.HasTranscript):
                UpdateTranscriptPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.Silence):
            case nameof(PipelineStep.HasSilence):
            case nameof(PipelineStep.SilenceRegionCount):
                UpdateSilencePane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.Chapters):
            case nameof(PipelineStep.HasChapters):
            case nameof(PipelineStep.ChapterCount):
                UpdateChaptersPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.ShowNotes):
            case nameof(PipelineStep.HasShowNotes):
                UpdateNotesPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.Highlights):
            case nameof(PipelineStep.HasHighlights):
            case nameof(PipelineStep.HighlightCount):
                UpdateHighlightsPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.ExportedClips):
            case nameof(PipelineStep.HasExportedClips):
            case nameof(PipelineStep.ExportedClipCount):
                UpdateClipsPane(step);
                UpdateArtifactsToggleVisibility(step);
                break;
            case nameof(PipelineStep.DeviceUsedLabel):
            case nameof(PipelineStep.HasDeviceUsed):
            case nameof(PipelineStep.DurationMs):
                UpdateDeviceBadge(step);
                break;
            case nameof(PipelineStep.CpuUsage):
                HwBars.CpuValue = step.CpuUsage;
                break;
            case nameof(PipelineStep.GpuUsage):
                HwBars.GpuValue = step.GpuUsage;
                break;
            case nameof(PipelineStep.NpuUsage):
                HwBars.NpuValue = step.NpuUsage;
                break;
            case nameof(PipelineStep.MemoryUsageMB):
                HwBars.MemoryValue = step.MemoryUsageMB;
                break;
        }
    }

    private void UpdateAll(PipelineStep step)
    {
        if (StepNameText == null) return;
        StepNameText.Text = step.Name;
        DescriptionText.Text = step.Description;
        StateIconElement.Glyph = step.StateIcon;
        EngineBadgeText.Text = step.EngineBadge;
        DurationTextBlock.Text = step.DurationText;
        RunningRing.Visibility = step.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        ResultSummaryText.Text = step.ResultSummary;
        ResultSummaryText.Visibility = step.HasResult ? Visibility.Visible : Visibility.Collapsed;
        LogText.Text = step.LogOutput;
        HwBars.CpuValue = step.CpuUsage;
        HwBars.GpuValue = step.GpuUsage;
        HwBars.NpuValue = step.NpuUsage;
        HwBars.MemoryValue = step.MemoryUsageMB;
        UpdatePreviewPane(step);
        UpdateDetectionsPane(step);
        UpdateTranscriptPane(step);
        UpdateSilencePane(step);
        UpdateChaptersPane(step);
        UpdateNotesPane(step);
        UpdateHighlightsPane(step);
        UpdateClipsPane(step);
        UpdateFilesPane(step);
        UpdateOptionsButton(step);
        UpdateDeviceBadge(step);
        UpdateArtifactsToggleVisibility(step);
        UpdateVisualState(step);
        RebuildPinChips(step);
    }

    private void ClearAll()
    {
        StepNameText.Text = string.Empty;
        DescriptionText.Text = string.Empty;
        StateIconElement.Glyph = string.Empty;
        EngineBadgeText.Text = string.Empty;
        DurationTextBlock.Text = string.Empty;
        RunningRing.Visibility = Visibility.Collapsed;
        ResultSummaryText.Text = string.Empty;
        ResultSummaryText.Visibility = Visibility.Collapsed;
        LogText.Text = string.Empty;
        HwBars.CpuValue = 0;
        HwBars.GpuValue = 0;
        HwBars.NpuValue = 0;
        HwBars.MemoryValue = 0;
        AccentBar.Background = new SolidColorBrush(Colors.Transparent);
        StateIconElement.Foreground = new SolidColorBrush(Colors.Gray);
        ArtifactsExpander.IsExpanded = false;
        ArtifactsExpander.Visibility = Visibility.Collapsed;
        OptionsButton.Visibility = Visibility.Collapsed;
        PinChipsRow.Children.Clear();
        PinChipsRow.Visibility = Visibility.Collapsed;
        _activeTab = null;
    }

    // ── Phase 8.8.5 — Input pin chips ────────────────────────────────
    private void RebuildPinChips(PipelineStep step)
    {
        PinChipsRow.Children.Clear();
        var effect = GetAllEffects().FirstOrDefault(e => e.Id == step.Id);
        if (effect == null) { PinChipsRow.Visibility = Visibility.Collapsed; return; }
        var pins = effect.EffectiveInputPins;
        if (pins == null || pins.Length == 0) { PinChipsRow.Visibility = Visibility.Collapsed; return; }
        PinChipsRow.Visibility = Visibility.Visible;

        foreach (var pin in pins)
        {
            step.Inputs.TryGetValue(pin.Id, out var refOrNull);
            PinChipsRow.Children.Add(BuildPinChip(step, pin, refOrNull));
        }
    }

    private FrameworkElement BuildPinChip(PipelineStep step, EffectPin pin, ArtifactRef? currentRef)
    {
        var hasRef = currentRef != null && !string.IsNullOrEmpty(currentRef.StepId);
        var teal = (SolidColorBrush)Application.Current.Resources["AccentTealBrush"];
        var dim = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x88, 0x88, 0x88));
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = hasRef ? teal : dim,
            Background = hasRef
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x0E, 0x8A, 0x7E))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Padding = new Thickness(8, 2, 8, 2),
        };
        string label;
        if (hasRef)
        {
            var producerName = ResolveProducerName(currentRef!.StepId);
            label = $"● {pin.Label}: {producerName}";
        }
        else
        {
            label = $"○ {pin.Label}{(pin.Required ? " *" : "")}";
        }
        var tb = new TextBlock { Text = label, FontSize = 11, Foreground = hasRef ? teal : dim };
        border.Child = tb;

        var btn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = border,
        };
        btn.Click += (_, _) => OpenPinFlyout(btn, step, pin, hasRef);
        return btn;
    }

    private string ResolveProducerName(string producerStepId)
    {
        if (ViewModels.PipelineViewModel.IsBucketStepId(producerStepId))
        {
            return "Input";
        }
        if (ViewModels.PipelineViewModel.IsSourceStepId(producerStepId))
        {
            var path = ViewModels.PipelineViewModel.SourcePathFromStepId(producerStepId);
            return $"src:{System.IO.Path.GetFileName(path)}";
        }
        if (GetPinCandidates != null)
        {
            // Find any candidate matching this producer to get its name.
            foreach (var pin in (Step?.Id is { } id ? GetAllEffects().FirstOrDefault(e => e.Id == id)?.EffectiveInputPins ?? System.Array.Empty<EffectPin>() : System.Array.Empty<EffectPin>()))
            {
                var cands = GetPinCandidates(pin);
                var hit = cands.FirstOrDefault(c => c.ProducerStepId == producerStepId);
                if (hit != null) return hit.ProducerName;
            }
        }
        return producerStepId.Length > 8 ? producerStepId.Substring(0, 8) : producerStepId;
    }

    private void OpenPinFlyout(FrameworkElement anchor, PipelineStep step, EffectPin pin, bool hasRef)
    {
        var flyout = new MenuFlyout();

        if (hasRef)
        {
            var clear = new MenuFlyoutItem { Text = $"Clear “{pin.Label}”" };
            clear.Click += (_, _) =>
            {
                PinSelectionChanged?.Invoke(pin.Id, null);
                if (Step is { } s) RebuildPinChips(s);
            };
            flyout.Items.Add(clear);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var candidates = GetPinCandidates?.Invoke(pin) ?? System.Array.Empty<PinCandidate>();
        if (candidates.Count == 0)
        {
            var none = new MenuFlyoutItem { Text = "(no compatible upstream producers)", IsEnabled = false };
            flyout.Items.Add(none);
        }
        else
        {
            foreach (var cand in candidates)
            {
                var item = new MenuFlyoutItem { Text = $"{cand.ProducerName} → {cand.ProducerPinId} ({cand.Kind})" };
                var captured = cand;
                item.Click += (_, _) =>
                {
                    PinSelectionChanged?.Invoke(pin.Id, new ArtifactRef
                    {
                        StepId = captured.ProducerStepId,
                        PinId = captured.ProducerPinId,
                        Kind = captured.Kind,
                    });
                    if (Step is { } s) RebuildPinChips(s);
                };
                flyout.Items.Add(item);
            }
        }
        flyout.ShowAt(anchor);
    }

    private void UpdateVisualState(PipelineStep step)
    {
        AccentBar.Background = step.State switch
        {
            PipelineStepState.Running => ResolveBrush("SystemAccentColor", Colors.DodgerBlue),
            PipelineStepState.Done => ResolveBrush("AccentTealBrush",
                Windows.UI.Color.FromArgb(255, 14, 138, 126)),
            PipelineStepState.Error => ResolveBrush("SystemFillColorCriticalBrush", Colors.Red),
            _ => new SolidColorBrush(Colors.Transparent)
        };

        StateIconElement.Foreground = step.State switch
        {
            PipelineStepState.Running => ResolveBrush("SystemAccentColor", Colors.DodgerBlue),
            PipelineStepState.Done => ResolveBrush("AccentTealBrush",
                Windows.UI.Color.FromArgb(255, 14, 138, 126)),
            PipelineStepState.Error => new SolidColorBrush(Colors.Red),
            _ => ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray)
        };
    }

    private Brush ResolveBrush(string key, Windows.UI.Color fallback)
    {
        if (Resources.TryGetValue(key, out var res))
        {
            if (res is Brush b) return b;
            if (res is Windows.UI.Color c) return new SolidColorBrush(c);
        }
        if (Application.Current.Resources.TryGetValue(key, out res))
        {
            if (res is Brush b) return b;
            if (res is Windows.UI.Color c) return new SolidColorBrush(c);
        }
        return new SolidColorBrush(fallback);
    }

    private bool _suppressOptionsHandlers;
    private ToggleButton? _activeTab;

    private void UpdateArtifactsToggleVisibility(PipelineStep step)
    {
        bool hasArtifacts = step.HasOutput
            || step.HasDetections
            || step.HasTranscript
            || step.HasSilence
            || step.HasChapters
            || step.HasShowNotes
            || step.HasHighlights
            || step.HasExportedClips
            || !string.IsNullOrEmpty(step.LogOutput)
            || !string.IsNullOrEmpty(step.InputVideoPath);
        ArtifactsExpander.Visibility = hasArtifacts ? Visibility.Visible : Visibility.Collapsed;

        // Tab visibility — gated per step type
        bool isTranscribe = string.Equals(step.Id, "transcribe", StringComparison.OrdinalIgnoreCase);
        bool isSilence = string.Equals(step.Id, "detect-silence", StringComparison.OrdinalIgnoreCase);
        bool isChapters = string.Equals(step.Id, "chapter-markers", StringComparison.OrdinalIgnoreCase);
        bool isShowNotes = string.Equals(step.Id, "show-notes", StringComparison.OrdinalIgnoreCase);
        bool isHighlights = string.Equals(step.Id, "highlight-picker", StringComparison.OrdinalIgnoreCase);
        bool isClips = string.Equals(step.Id, "export-clips", StringComparison.OrdinalIgnoreCase);
        TabTranscript.Visibility = isTranscribe ? Visibility.Visible : Visibility.Collapsed;
        TabSilence.Visibility = isSilence ? Visibility.Visible : Visibility.Collapsed;
        TabChapters.Visibility = isChapters ? Visibility.Visible : Visibility.Collapsed;
        TabNotes.Visibility = isShowNotes ? Visibility.Visible : Visibility.Collapsed;
        TabHighlights.Visibility = isHighlights ? Visibility.Visible : Visibility.Collapsed;
        TabClips.Visibility = isClips ? Visibility.Visible : Visibility.Collapsed;
        TabDetections.Visibility = string.Equals(step.Id, "detect-objects", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;

        if (hasArtifacts && _activeTab == null)
        {
            // Default-select the first non-empty tab in priority order
            if (isTranscribe && step.HasTranscript) SelectTab(TabTranscript);
            else if (isSilence && step.HasSilence) SelectTab(TabSilence);
            else if (isChapters && step.HasChapters) SelectTab(TabChapters);
            else if (isShowNotes && step.HasShowNotes) SelectTab(TabNotes);
            else if (isHighlights && step.HasHighlights) SelectTab(TabHighlights);
            else if (isClips && step.HasExportedClips) SelectTab(TabClips);
            else if (step.HasOutput) SelectTab(TabPreview);
            else if (step.HasDetections) SelectTab(TabDetections);
            else if (!string.IsNullOrEmpty(step.LogOutput)) SelectTab(TabLogs);
            else SelectTab(TabFiles);
        }
    }

    private void UpdateOptionsButton(PipelineStep step)
    {
        bool isDetect = string.Equals(step.Id, "detect-objects", StringComparison.OrdinalIgnoreCase);
        bool isTranscribe = string.Equals(step.Id, "transcribe", StringComparison.OrdinalIgnoreCase);
        bool isSilence = string.Equals(step.Id, "detect-silence", StringComparison.OrdinalIgnoreCase);
        bool isSmartCut = string.Equals(step.Id, "smart-cut", StringComparison.OrdinalIgnoreCase);
        bool isChapters = string.Equals(step.Id, "chapter-markers", StringComparison.OrdinalIgnoreCase);
        bool isHighlights = string.Equals(step.Id, "highlight-picker", StringComparison.OrdinalIgnoreCase);
        bool isShowNotes = string.Equals(step.Id, "show-notes", StringComparison.OrdinalIgnoreCase);
        bool isWinML = step.Engine == EngineType.WindowsML;
        bool usesLm = isChapters || isHighlights || isShowNotes;

        OptionsButton.Visibility = (isDetect || isTranscribe || isSilence || isSmartCut || isChapters || isHighlights || isShowNotes || isWinML) ? Visibility.Visible : Visibility.Collapsed;
        OptionsConfidencePanel.Visibility = isDetect ? Visibility.Visible : Visibility.Collapsed;
        OptionsFramesPanel.Visibility = isDetect ? Visibility.Visible : Visibility.Collapsed;
        OptionsWhisperPanel.Visibility = isTranscribe ? Visibility.Visible : Visibility.Collapsed;
        OptionsSilencePanel.Visibility = isSilence ? Visibility.Visible : Visibility.Collapsed;
        OptionsSmartCutPanel.Visibility = isSmartCut ? Visibility.Visible : Visibility.Collapsed;
        OptionsChaptersPanel.Visibility = isChapters ? Visibility.Visible : Visibility.Collapsed;
        OptionsHighlightsPanel.Visibility = isHighlights ? Visibility.Visible : Visibility.Collapsed;
        OptionsLanguageModelPanel.Visibility = usesLm ? Visibility.Visible : Visibility.Collapsed;
        // Hardware combo only makes sense for steps that actually pick an EP. DSP/ffmpeg/Phi-Silica steps don't.
        OptionsHardwarePanel.Visibility = (isWinML && !isSilence && !isSmartCut) ? Visibility.Visible : Visibility.Collapsed;

        _suppressOptionsHandlers = true;
        if (isDetect)
        {
            OptionsConfidenceSlider.Value = Math.Clamp(step.ConfidenceThreshold * 100.0, 5, 95);
            OptionsFramesSlider.Value = Math.Clamp(step.SampleFrameCount, 15, 180);
            OptionsConfidenceValueText.Text = $"{(int)OptionsConfidenceSlider.Value}%";
            OptionsFramesValueText.Text = $"{(int)OptionsFramesSlider.Value}";
        }
        if (isTranscribe)
        {
            OptionsWhisperModelCombo.SelectedIndex = (int)step.WhisperModel;
            EnsureLanguageItems();
            var lang = step.TranscribeLanguage ?? "en";
            for (int i = 0; i < OptionsWhisperLanguageCombo.Items.Count; i++)
            {
                if (OptionsWhisperLanguageCombo.Items[i] is string s && s.StartsWith(lang + " "))
                {
                    OptionsWhisperLanguageCombo.SelectedIndex = i;
                    break;
                }
            }
            if (OptionsWhisperLanguageCombo.SelectedIndex < 0)
                OptionsWhisperLanguageCombo.SelectedIndex = 0;
            OptionsWhisperTranslateToggle.IsOn = step.TranscribeTranslate;
        }
        if (isSilence)
        {
            OptionsSilenceThreshSlider.Value = Math.Clamp(step.SilenceThresholdDb, -60, -20);
            OptionsSilenceMinSlider.Value = Math.Clamp(step.MinSilenceMs, 100, 3000);
            OptionsSilencePadSlider.Value = Math.Clamp(step.SilencePadMs, 0, 1000);
            OptionsSilenceThreshValueText.Text = $"{(int)OptionsSilenceThreshSlider.Value} dBFS";
            OptionsSilenceMinValueText.Text = $"{(int)OptionsSilenceMinSlider.Value} ms";
            OptionsSilencePadValueText.Text = $"{(int)OptionsSilencePadSlider.Value} ms";
        }
        if (isSmartCut)
        {
            OptionsMinKeepSlider.Value = Math.Clamp(step.MinKeepMs, 0, 2000);
            OptionsMinKeepValueText.Text = $"{(int)OptionsMinKeepSlider.Value} ms";
        }
        if (isChapters)
        {
            OptionsChapterCountSlider.Value = Math.Clamp(step.ChapterTargetCount, 2, 20);
            OptionsChapterCountValueText.Text = $"{(int)OptionsChapterCountSlider.Value}";
        }
        if (isHighlights)
        {
            OptionsHighlightCountSlider.Value = Math.Clamp(step.HighlightTargetCount, 1, 10);
            OptionsHighlightCountValueText.Text = $"{(int)OptionsHighlightCountSlider.Value}";
            OptionsHighlightClipSecSlider.Value = Math.Clamp(step.HighlightClipSeconds, 10, 120);
            OptionsHighlightClipSecValueText.Text = $"{(int)OptionsHighlightClipSecSlider.Value}s";
        }
        if (isWinML && !isSilence && !isSmartCut)
        {
            OptionsHardwareCombo.SelectedIndex = (int)step.HardwarePreference;
        }
        if (usesLm)
        {
            if (OptionsLanguageModelCombo.ItemsSource == null)
                OptionsLanguageModelCombo.ItemsSource = Services.LanguageModelRegistry.Catalog;
            var id = step.LanguageModelBackendId ?? Services.PhiSilicaLanguageModel.BackendId;
            int sel = -1;
            for (int i = 0; i < Services.LanguageModelRegistry.Catalog.Count; i++)
            {
                if (string.Equals(Services.LanguageModelRegistry.Catalog[i].Id, id, StringComparison.OrdinalIgnoreCase))
                { sel = i; break; }
            }
            OptionsLanguageModelCombo.SelectedIndex = sel >= 0 ? sel : 0;
            var entry = OptionsLanguageModelCombo.SelectedItem as Services.LanguageModelRegistry.Entry;
            OptionsLanguageModelTagline.Text = entry?.Tagline ?? string.Empty;
        }
        _suppressOptionsHandlers = false;
    }

    private void OnOptionsLanguageModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        if (OptionsLanguageModelCombo.SelectedItem is not Services.LanguageModelRegistry.Entry entry) return;
        step.LanguageModelBackendId = entry.Id;
        OptionsLanguageModelTagline.Text = entry.Tagline ?? string.Empty;
    }

    private bool _languageItemsInitialized;
    private void EnsureLanguageItems()
    {
        if (_languageItemsInitialized) return;
        _languageItemsInitialized = true;
        OptionsWhisperLanguageCombo.Items.Clear();
        foreach (var code in Services.WhisperRunner.SupportedLanguages)
            OptionsWhisperLanguageCombo.Items.Add($"{code}  ({DescribeLanguage(code)})");
    }

    private static string DescribeLanguage(string code) => code switch
    {
        "en" => "English", "zh" => "Chinese", "de" => "German", "es" => "Spanish",
        "ru" => "Russian", "ko" => "Korean", "fr" => "French", "ja" => "Japanese",
        "pt" => "Portuguese", "tr" => "Turkish", "pl" => "Polish", "ca" => "Catalan",
        "nl" => "Dutch", "ar" => "Arabic", "sv" => "Swedish", "it" => "Italian",
        "id" => "Indonesian", "hi" => "Hindi", "fi" => "Finnish", "vi" => "Vietnamese",
        "he" => "Hebrew", "uk" => "Ukrainian", "el" => "Greek", "ms" => "Malay",
        "cs" => "Czech", "ro" => "Romanian", "da" => "Danish", "hu" => "Hungarian",
        "ta" => "Tamil", "no" => "Norwegian", "th" => "Thai",
        _ => code
    };

    private void OnOptionsWhisperModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        if (OptionsWhisperModelCombo.SelectedIndex < 0) return;
        step.WhisperModel = (Services.WhisperModelSize)OptionsWhisperModelCombo.SelectedIndex;
    }

    private void OnOptionsWhisperLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        if (OptionsWhisperLanguageCombo.SelectedItem is string s)
        {
            var code = s.Split(' ')[0];
            step.TranscribeLanguage = code;
        }
    }

    private void OnOptionsWhisperTranslateToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.TranscribeTranslate = OptionsWhisperTranslateToggle.IsOn;
    }

    private void OnOptionsHardwareChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        if (OptionsHardwareCombo.SelectedIndex < 0) return;
        step.HardwarePreference = (Services.HardwarePreference)OptionsHardwareCombo.SelectedIndex;
    }

    private void OnOptionsSilenceThreshChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.SilenceThresholdDb = OptionsSilenceThreshSlider.Value;
        OptionsSilenceThreshValueText.Text = $"{(int)OptionsSilenceThreshSlider.Value} dBFS";
    }

    private void OnOptionsSilenceMinChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.MinSilenceMs = (int)OptionsSilenceMinSlider.Value;
        OptionsSilenceMinValueText.Text = $"{(int)OptionsSilenceMinSlider.Value} ms";
    }

    private void OnOptionsSilencePadChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.SilencePadMs = (int)OptionsSilencePadSlider.Value;
        OptionsSilencePadValueText.Text = $"{(int)OptionsSilencePadSlider.Value} ms";
    }

    private void OnOptionsMinKeepChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.MinKeepMs = (int)OptionsMinKeepSlider.Value;
        OptionsMinKeepValueText.Text = $"{(int)OptionsMinKeepSlider.Value} ms";
    }

    private void OnOptionsChapterCountChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.ChapterTargetCount = (int)OptionsChapterCountSlider.Value;
        OptionsChapterCountValueText.Text = $"{(int)OptionsChapterCountSlider.Value}";
    }

    private void OnOptionsHighlightCountChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.HighlightTargetCount = (int)OptionsHighlightCountSlider.Value;
        OptionsHighlightCountValueText.Text = $"{(int)OptionsHighlightCountSlider.Value}";
    }

    private void OnOptionsHighlightClipSecChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.HighlightClipSeconds = (int)OptionsHighlightClipSecSlider.Value;
        OptionsHighlightClipSecValueText.Text = $"{(int)OptionsHighlightClipSecSlider.Value}s";
    }

    private void UpdateChaptersPane(PipelineStep step)
    {
        if (step.HasChapters && step.Chapters is { } c)
        {
            ChaptersList.ItemsSource = c.Chapters;
            ChaptersList.Visibility = Visibility.Visible;
            ChaptersEmptyText.Visibility = Visibility.Collapsed;
            string fallbackTag = c.UsedFallback ? "  ⚠ heuristic fallback" : "";
            ChaptersSummary.Text =
                $"{c.Chapters.Count} chapters • {c.ElapsedMs} ms on {c.DeviceUsed}{fallbackTag}";
        }
        else
        {
            ChaptersList.ItemsSource = null;
            ChaptersList.Visibility = Visibility.Collapsed;
            ChaptersEmptyText.Visibility = Visibility.Visible;
            ChaptersSummary.Text = string.Empty;
        }
    }

    private void UpdateNotesPane(PipelineStep step)
    {
        if (NotesContent == null) return;
        NotesContent.Children.Clear();

        if (step.HasShowNotes && step.ShowNotes is { } n)
        {
            string fallbackTag = n.UsedFallback ? "  ⚠ heuristic fallback" : "";
            NotesSummary.Text = $"{n.Blocks.Count} sections • {n.ElapsedMs} ms on {n.DeviceUsed}{fallbackTag}";
            NotesScrollViewer.Visibility = Visibility.Visible;
            NotesEmptyText.Visibility = Visibility.Collapsed;
            CopyNotesButton.IsEnabled = !string.IsNullOrEmpty(n.Markdown);

            foreach (var block in n.Blocks)
            {
                NotesContent.Children.Add(BuildNotesBlockElement(block));
            }
        }
        else
        {
            NotesSummary.Text = string.Empty;
            NotesScrollViewer.Visibility = Visibility.Collapsed;
            NotesEmptyText.Visibility = Visibility.Visible;
            CopyNotesButton.IsEnabled = false;
        }
    }

    private static FrameworkElement BuildNotesBlockElement(ShowNotesBlock block)
    {
        switch (block.Kind)
        {
            case ShowNotesBlockKind.Heading:
                return new TextBlock
                {
                    Text = block.Text,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = block.Level switch { 1 => 18, 2 => 15, _ => 13 },
                    Margin = new Thickness(0, block.Level == 2 ? 8 : 4, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };

            case ShowNotesBlockKind.Bullet:
                {
                    var grid = new Grid { Margin = new Thickness(8, 0, 0, 0) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var bullet = new TextBlock
                    {
                        Text = "•",
                        FontSize = 13,
                        Foreground = (Brush)Application.Current.Resources["AccentTealBrush"],
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    Grid.SetColumn(bullet, 0);
                    grid.Children.Add(bullet);
                    var body = new TextBlock
                    {
                        Text = block.Text,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    };
                    Grid.SetColumn(body, 1);
                    grid.Children.Add(body);
                    return grid;
                }

            case ShowNotesBlockKind.Paragraph:
            default:
                return new TextBlock
                {
                    Text = block.Text,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                };
        }
    }

    private void OnCopyNotesClick(object sender, RoutedEventArgs e)
    {
        if (Step?.ShowNotes is not { } n || string.IsNullOrEmpty(n.Markdown)) return;
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(n.Markdown);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch
        {
            // Clipboard can fail in some headless contexts — best-effort copy.
        }
    }

    private void UpdateHighlightsPane(PipelineStep step)
    {
        if (HighlightsList == null) return;
        if (step.HasHighlights && step.Highlights is { } h)
        {
            HighlightsList.ItemsSource = h.Highlights;
            HighlightsList.Visibility = Visibility.Visible;
            HighlightsEmptyText.Visibility = Visibility.Collapsed;
            string fallbackTag = h.UsedFallback ? "  ⚠ heuristic fallback" : "";
            HighlightsSummary.Text =
                $"{h.Highlights.Count} highlights • {h.ElapsedMs} ms on {h.DeviceUsed}{fallbackTag}";
        }
        else
        {
            HighlightsList.ItemsSource = null;
            HighlightsList.Visibility = Visibility.Collapsed;
            HighlightsEmptyText.Visibility = Visibility.Visible;
            HighlightsSummary.Text = string.Empty;
        }
    }

    private void UpdateClipsPane(PipelineStep step)
    {
        if (ClipsList == null) return;
        if (step.HasExportedClips)
        {
            ClipsList.ItemsSource = step.ExportedClips;
            ClipsList.Visibility = Visibility.Visible;
            ClipsEmptyText.Visibility = Visibility.Collapsed;
            OpenClipsFolderButton.Visibility = Visibility.Visible;
            long totalBytes = 0;
            foreach (var c in step.ExportedClips) totalBytes += c.FileSizeBytes;
            ClipsSummary.Text = $"{step.ExportedClips.Count} clips • {totalBytes / 1024.0 / 1024.0:F1} MB total";
        }
        else
        {
            ClipsList.ItemsSource = null;
            ClipsList.Visibility = Visibility.Collapsed;
            ClipsEmptyText.Visibility = Visibility.Visible;
            OpenClipsFolderButton.Visibility = Visibility.Collapsed;
            ClipsSummary.Text = string.Empty;
        }
    }

    private void UpdateSilencePane(PipelineStep step)
    {
        if (step.HasSilence && step.Silence is { } s)
        {
            SilenceList.ItemsSource = s.Intervals;
            SilenceList.Visibility = s.Intervals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SilenceEmptyText.Visibility = s.Intervals.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            SilenceSummary.Text =
                $"{s.Intervals.Count} silent regions • {s.TotalSilenceSec:F1}s of {s.AudioDurationSec:F1}s " +
                $"({s.Coverage * 100:F0}%) • threshold {s.ThresholdDb:F0} dBFS";
            DrawSilenceTimeline();
        }
        else
        {
            SilenceList.ItemsSource = null;
            SilenceList.Visibility = Visibility.Collapsed;
            SilenceEmptyText.Visibility = Visibility.Collapsed;
            SilenceSummary.Text = string.Empty;
            SilenceTimelineCanvas.Children.Clear();
        }
    }

    private void OnSilenceTimelineSizeChanged(object sender, SizeChangedEventArgs e) => DrawSilenceTimeline();

    private void DrawSilenceTimeline()
    {
        SilenceTimelineCanvas.Children.Clear();
        if (Step is not { } step || step.Silence is not { } s || s.AudioDurationSec <= 0) return;
        double w = SilenceTimelineCanvas.ActualWidth;
        double h = SilenceTimelineCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var brush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed) { Opacity = 0.75 };
        foreach (var iv in s.Intervals)
        {
            double x = iv.Start / s.AudioDurationSec * w;
            double rectW = Math.Max(1.5, iv.Duration / s.AudioDurationSec * w);
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = rectW,
                Height = h,
                Fill = brush
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            SilenceTimelineCanvas.Children.Add(rect);
        }
    }

    private void UpdateTranscriptPane(PipelineStep step)
    {
        if (step.HasTranscript && step.Transcript is { } t)
        {
            TranscriptList.ItemsSource = t.Segments;
            TranscriptList.Visibility = Visibility.Visible;
            TranscriptEmptyText.Visibility = Visibility.Collapsed;
            TranscriptSummary.Text =
                $"{t.Segments.Count} segments • {t.AudioDurationSeconds:F0}s audio • " +
                $"{t.ElapsedMs / 1000.0:F1}s on {t.DeviceUsed} ({t.RealtimeFactor:F1}× realtime)";
        }
        else
        {
            TranscriptList.ItemsSource = null;
            TranscriptList.Visibility = Visibility.Collapsed;
            TranscriptEmptyText.Visibility = Visibility.Visible;
            TranscriptSummary.Text = string.Empty;
        }
    }

    private void UpdateDeviceBadge(PipelineStep step)
    {
        if (step.HasDeviceUsed && step.IsDone)
        {
            DeviceBadgeText.Text = $"Ran on {step.DeviceUsedLabel}" +
                (step.DurationMs > 0 ? $" • {step.DurationText}" : "");
            DeviceBadge.Visibility = Visibility.Visible;
        }
        else
        {
            DeviceBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOptionsConfidenceChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OptionsConfidenceValueText.Text = $"{(int)e.NewValue}%";
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.ConfidenceThreshold = e.NewValue / 100.0;
    }

    private void OnOptionsFramesChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OptionsFramesValueText.Text = $"{(int)e.NewValue}";
        if (_suppressOptionsHandlers || Step is not { } step) return;
        step.SampleFrameCount = (int)e.NewValue;
    }

    private string? _currentPreviewPath;

    private async void UpdatePreviewPane(PipelineStep step)
    {
        string? path = step.OutputVideoPath ?? step.InputVideoPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _currentPreviewPath = null;
            PreviewThumbImage.Source = null;
            PreviewPlayButton.Visibility = Visibility.Collapsed;
            PreviewEmptyText.Visibility = Visibility.Visible;
            return;
        }

        PreviewPlayButton.Visibility = Visibility.Visible;
        PreviewEmptyText.Visibility = Visibility.Collapsed;

        if (path == _currentPreviewPath && PreviewThumbImage.Source != null) return;
        _currentPreviewPath = path;

        try
        {
            var bmp = await VideoStudio.Services.ThumbnailService.ExtractSingleThumbnailAsync(
                path, timeOffset: TimeSpan.FromMilliseconds(500), width: 480, height: 270);
            // Path may have changed while loading; only assign if still current.
            if (path == _currentPreviewPath)
                PreviewThumbImage.Source = bmp;
        }
        catch
        {
            // Leave the play button + empty image visible — Play still works.
        }
    }

    private void OnPreviewPlayClick(object sender, RoutedEventArgs e)
    {
        var path = _currentPreviewPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void UpdateDetectionsPane(PipelineStep step)
    {
        if (step.HasDetections)
        {
            var items = step.Detections.Select(DetectionViewItem.From).ToList();
            DetectionsList.ItemsSource = items;
            DetectionsList.Visibility = Visibility.Visible;
            DetectionsEmptyText.Visibility = Visibility.Collapsed;
            var counts = items.GroupBy(i => i.Label)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{g.Key} ×{g.Count()}");
            DetectionsSummary.Text = $"{items.Count} detections — {string.Join(", ", counts.Take(6))}"
                + (counts.Count() > 6 ? "…" : "");
        }
        else
        {
            DetectionsList.ItemsSource = null;
            DetectionsList.Visibility = Visibility.Collapsed;
            DetectionsEmptyText.Visibility = Visibility.Visible;
            DetectionsSummary.Text = string.Empty;
        }
    }

    private void UpdateFilesPane(PipelineStep step)
    {
        InputPathText.Text = step.InputVideoPath ?? "(none)";
        OutputPathText.Text = step.OutputVideoPath ?? "(not yet generated)";
        OpenInputFolderButton.IsEnabled = !string.IsNullOrEmpty(step.InputVideoPath) && File.Exists(step.InputVideoPath);
        OpenOutputFolderButton.IsEnabled = !string.IsNullOrEmpty(step.OutputVideoPath) && File.Exists(step.OutputVideoPath);
    }

    private void SelectTab(ToggleButton tab)
    {
        _activeTab = tab;
        TabPreview.IsChecked = tab == TabPreview;
        TabDetections.IsChecked = tab == TabDetections;
        TabTranscript.IsChecked = tab == TabTranscript;
        TabSilence.IsChecked = tab == TabSilence;
        TabChapters.IsChecked = tab == TabChapters;
        TabNotes.IsChecked = tab == TabNotes;
        TabHighlights.IsChecked = tab == TabHighlights;
        TabClips.IsChecked = tab == TabClips;
        TabLogs.IsChecked = tab == TabLogs;
        TabFiles.IsChecked = tab == TabFiles;
        PreviewPane.Visibility = tab == TabPreview ? Visibility.Visible : Visibility.Collapsed;
        DetectionsPane.Visibility = tab == TabDetections ? Visibility.Visible : Visibility.Collapsed;
        TranscriptPane.Visibility = tab == TabTranscript ? Visibility.Visible : Visibility.Collapsed;
        SilencePane.Visibility = tab == TabSilence ? Visibility.Visible : Visibility.Collapsed;
        ChaptersPane.Visibility = tab == TabChapters ? Visibility.Visible : Visibility.Collapsed;
        NotesPane.Visibility = tab == TabNotes ? Visibility.Visible : Visibility.Collapsed;
        HighlightsPane.Visibility = tab == TabHighlights ? Visibility.Visible : Visibility.Collapsed;
        ClipsPane.Visibility = tab == TabClips ? Visibility.Visible : Visibility.Collapsed;
        LogsPane.Visibility = tab == TabLogs ? Visibility.Visible : Visibility.Collapsed;
        FilesPane.Visibility = tab == TabFiles ? Visibility.Visible : Visibility.Collapsed;

        if (tab == TabSilence) DrawSilenceTimeline();
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            // Always end up with this tab checked even if user clicks the active one (no toggle-off)
            SelectTab(tb);
        }
    }

    private void OnOpenInputFolderClick(object sender, RoutedEventArgs e) => RevealInExplorer(Step?.InputVideoPath);
    private void OnOpenOutputFolderClick(object sender, RoutedEventArgs e) => RevealInExplorer(Step?.OutputVideoPath);

    private void OnOpenClipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OnOpenClipsFolderClick(object sender, RoutedEventArgs e)
    {
        var clip = Step?.ExportedClips?.FirstOrDefault();
        if (clip == null || string.IsNullOrEmpty(clip.OutputPath)) return;
        var dir = Path.GetDirectoryName(clip.OutputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnRunStepClick(object sender, RoutedEventArgs e) =>
        RunStepRequested?.Invoke(this, EventArgs.Empty);

    private void OnRemoveClick(object sender, RoutedEventArgs e) =>
        RemoveRequested?.Invoke(this, EventArgs.Empty);

    private void OnMoveUpClick(object sender, RoutedEventArgs e) =>
        MoveUpRequested?.Invoke(this, EventArgs.Empty);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) =>
        MoveDownRequested?.Invoke(this, EventArgs.Empty);
}

