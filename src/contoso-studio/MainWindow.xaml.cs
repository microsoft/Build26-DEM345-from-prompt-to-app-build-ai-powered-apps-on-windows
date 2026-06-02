using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using VideoStudio.Controls;
using VideoStudio.Models;
using VideoStudio.Services;
using VideoStudio.ViewModels;

namespace VideoStudio;

public sealed partial class MainWindow : Window
{
    public PipelineViewModel PipelineVM { get; }
    private readonly HardwareMonitor _hardwareMonitor;

    public MainWindow()
    {
        InitializeComponent();

        PipelineVM = new PipelineViewModel();
        _hardwareMonitor = new HardwareMonitor(DispatcherQueue);
        PipelineVM.SetHardwareMonitor(_hardwareMonitor);

        // Phase 8.8.5 — let cards look up effect metadata by id
        VideoStudio.Controls.PipelineStepCard.GetAllEffects = () => PipelineVM.AvailableEffects;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Wire effect lists to initial add button (Phase 8.9 — sources/initial-add now on canvas; legacy stubs).
        // InitialAddButton.ContainerEffects = PipelineVM.ContainerEffects;
        // InitialAddButton.NativeEffects = PipelineVM.NativeEffects;

        // Rebuild pipeline UI when steps collection changes
        PipelineVM.Steps.CollectionChanged += OnStepsCollectionChanged;

        // Push ViewModel property changes to UI controls
        PipelineVM.PropertyChanged += OnPipelineVMPropertyChanged;

        // Update sources list when MediaFiles collection changes
        PipelineVM.MediaFiles.CollectionChanged += OnMediaFilesCollectionChanged;
        // If the VM restored previously-imported media before we attached the handler above,
        // sync the sidebar + canvas now so the saved input shows up on launch.
        if (PipelineVM.MediaFiles.Count > 0)
        {
            OnMediaFilesCollectionChanged(PipelineVM.MediaFiles, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        // Hardware monitor telemetry → status bar sparklines
        _hardwareMonitor.HistoryUpdated += OnHardwareHistoryUpdated;
        _hardwareMonitor.Start();

        // Populate sidebar effects and sources
        PopulateSidebar();

        // Populate Notebook → Templates submenu from bundled .studionb files
        PopulateNotebookTemplates();

        // Auto-load test files if available (for demo/testing)
        string testVideo = @"D:\contoso-studio\TestData\people-walking.mp4";
        string testAudio = @"D:\contoso-studio\TestData\test-speech.wav";
        // Override via env var (used for E2E test with arbitrary media)
        var envOverride = System.Environment.GetEnvironmentVariable("CONTOSO_AUTOIMPORT");
        var toImport = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            foreach (var p in envOverride.Split('|', System.StringSplitOptions.RemoveEmptyEntries))
                if (System.IO.File.Exists(p)) toImport.Add(p);
        }
        if (toImport.Count == 0)
        {
            if (System.IO.File.Exists(testVideo)) toImport.Add(testVideo);
            if (System.IO.File.Exists(testAudio)) toImport.Add(testAudio);
        }
        if (toImport.Count > 0)
        {
            PipelineVM.ImportFilesCommand.Execute(toImport.ToArray());
        }

        // CONTOSO_BATCH or .test-batch marker: pipe-separated paths to seed BatchSources.
        var batchEnv = System.Environment.GetEnvironmentVariable("CONTOSO_BATCH");
        var batchMarker = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".contoso-test-batch");
        if (string.IsNullOrWhiteSpace(batchEnv) && System.IO.File.Exists(batchMarker))
        {
            try { batchEnv = System.IO.File.ReadAllText(batchMarker).Trim(); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(batchEnv))
        {
            foreach (var p in batchEnv.Split('|', System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (System.IO.File.Exists(p))
                {
                    PipelineVM.AddBatchSourceCommand.Execute(new VideoStudio.Models.MediaFile(p));
                }
            }
        }

        // CONTOSO_AUTOSTEPS or .test-autosteps marker: comma-separated effect ids.
        var stepsEnv = System.Environment.GetEnvironmentVariable("CONTOSO_AUTOSTEPS");
        var stepsMarker = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".contoso-test-autosteps");
        if (string.IsNullOrWhiteSpace(stepsEnv) && System.IO.File.Exists(stepsMarker))
        {
            try { stepsEnv = System.IO.File.ReadAllText(stepsMarker).Trim(); } catch { }
        }
        if (!string.IsNullOrWhiteSpace(stepsEnv))
        {
            foreach (var id in stepsEnv.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            {
                var effect = PipelineVM.AvailableEffects.FirstOrDefault(e => string.Equals(e.Id, id.Trim(), System.StringComparison.OrdinalIgnoreCase));
                if (effect != null) PipelineVM.InsertStep(-1, effect);
            }
        }
    }

    // ── ViewModel property → UI push ────────────────────────────────

    private void OnPipelineVMPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PipelineVM.SourceFile):
                UpdateSourceCard();
                UpdateOutputCard();
                UpdateSourcesListSelection();
                break;
            case nameof(PipelineVM.ShowInfoBar):
            case nameof(PipelineVM.InfoBarMessage):
            case nameof(PipelineVM.InfoBarSeverity):
                // Route info bar messages to status bar instead
                if (PipelineVM.ShowInfoBar && !string.IsNullOrEmpty(PipelineVM.InfoBarMessage))
                    StatusBarText.Text = PipelineVM.InfoBarMessage;
                break;
            case nameof(PipelineVM.StatusMessage):
                StatusBarText.Text = PipelineVM.StatusMessage ?? string.Empty;
                break;
            case nameof(PipelineVM.IsRunning):
                RunAllButton.IsEnabled = !PipelineVM.IsRunning && PipelineVM.CanRun;
                StopButton.IsEnabled = PipelineVM.IsRunning;
                break;
            case nameof(PipelineVM.CanRun):
                RunAllButton.IsEnabled = PipelineVM.CanRun;
                break;
        }
    }

    private void UpdateSourceCard()
    {
        // Phase 8.9 — source card replaced by canvas source nodes (no-op).
    }

    private void UpdateOutputCard()
    {
        // Phase 8.9 — output card replaced by per-step output rendered on canvas (no-op).
    }

    // (InfoBar removed — status messages go to status bar)

    // ── Pipeline steps UI rebuild ───────────────────────────────────

    private void OnStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPipelineUI();
        UpdateOutputCard();
    }

    private void RebuildPipelineUI()
    {
        try
        {
            StepsPanel.Children.Clear();

            // Phase 8.9 — LTR DAG. Pass MediaFiles so source nodes render on the canvas.
            var layout = VideoStudio.Services.GraphLayout.Build(
                PipelineVM.MediaFiles,
                PipelineVM.Steps,
                MeasuredHeightFor,
                WidthForStep);

            foreach (var node in layout.Nodes)
            {
                if (node.IsBucket)
                {
                    var bucketCard = BuildBucketNode(node);
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(bucketCard, node.X);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(bucketCard, node.Y);
                    StepsPanel.Children.Add(bucketCard);
                    continue;
                }
                if (node.Step == null) continue;
                var step = node.Step;

                var card = new PipelineStepCard
                {
                    CanDrag = true,
                    AllowDrop = true,
                    Width = node.Width,
                };
                card.DragStarting += StepCard_DragStarting;
                card.DragOver += StepCard_DragOver;
                card.Drop += StepCard_Drop;
                card.RunStepRequested += (s, _) => PipelineVM.RunStepCommand.Execute(step);
                card.CancelStepRequested += (s, _) => PipelineVM.StopExecutionCommand.Execute(null);
                card.RemoveRequested += (s, _) => PipelineVM.RemoveStepCommand.Execute(step);
                card.SizeChanged += OnStepCardSizeChanged;
                card.SelectionRequested += OnStepCardSelectionRequested;
                card.IsCardSelected = step.StepId == _selectedStepId;
                card.Tag = step;

                // Phase 8.8.5 — pin chip wiring
                card.GetPinCandidates = pin => GetCandidatesFor(step, pin);
                card.PinSelectionChanged = (pinId, newRef) => ApplyPinSelection(step, pinId, newRef);

                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(card, node.X);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(card, node.Y);
                StepsPanel.Children.Add(card);
                card.Step = step; // Set AFTER adding to visual tree so XAML elements exist
            }

            DrawWires(layout);

            StepsPanel.Width = layout.TotalWidth;
            StepsPanel.Height = layout.TotalHeight;
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"UI Error: {ex.Message} | {ex.StackTrace?.Split('\n').FirstOrDefault()}";
        }
    }

    private void DrawWires(VideoStudio.Services.GraphLayout.GraphLayoutResult layout)
    {
        // LTR: producer right-center → consumer left-center, with horizontal bezier control points.
        foreach (var node in layout.Nodes)
        {
            if (node.Step == null) continue;
            var consumer = node;
            foreach (var kv in node.Step.Inputs)
            {
                var refStepId = kv.Value?.StepId;
                if (string.IsNullOrEmpty(refStepId)) continue;
                var producer = layout.FindByNodeId(refStepId);
                if (producer == null) continue; // producer not on canvas (shouldn't happen now that sources render)

                var x1 = producer.X + producer.Width;
                var y1 = producer.Y + producer.Height / 2;
                var x2 = consumer.X;
                var y2 = consumer.Y + consumer.Height / 2;

                var dx = System.Math.Max(40, (x2 - x1) / 2);
                var fig = new Microsoft.UI.Xaml.Media.PathFigure
                {
                    StartPoint = new Windows.Foundation.Point(x1, y1),
                    IsClosed = false,
                };
                fig.Segments.Add(new Microsoft.UI.Xaml.Media.BezierSegment
                {
                    Point1 = new Windows.Foundation.Point(x1 + dx, y1),
                    Point2 = new Windows.Foundation.Point(x2 - dx, y2),
                    Point3 = new Windows.Foundation.Point(x2, y2),
                });
                var geo = new Microsoft.UI.Xaml.Media.PathGeometry();
                geo.Figures.Add(fig);

                var path = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geo,
                    Stroke = WireBrushFor(kv.Value!.Kind),
                    StrokeThickness = 2,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(path, -1);
                StepsPanel.Children.Insert(0, path);
            }
        }
    }

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WireVideoBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x0E, 0x8A, 0x7E));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WireAudioBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xE8, 0x7D, 0x2F));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WireTextBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x66, 0x99, 0xFF));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WireDefaultBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x80, 0x80, 0x80));

    private static Microsoft.UI.Xaml.Media.Brush WireBrushFor(VideoStudio.Models.ArtifactKind kind) => kind switch
    {
        VideoStudio.Models.ArtifactKind.Video => WireVideoBrush,
        VideoStudio.Models.ArtifactKind.Audio => WireAudioBrush,
        VideoStudio.Models.ArtifactKind.Transcript => WireTextBrush,
        VideoStudio.Models.ArtifactKind.Text => WireTextBrush,
        _ => WireDefaultBrush,
    };

    // Tracks measured card heights so the next layout pass packs rows tightly when cards expand.
    private readonly System.Collections.Generic.Dictionary<VideoStudio.Models.PipelineStep, double> _stepCardActualHeight = new();
    private bool _layoutDirtyDuringAnimation;

    private double MeasuredHeightFor(VideoStudio.Models.PipelineStep step) =>
        _stepCardActualHeight.TryGetValue(step, out var h) ? h : 0;

    // ===== Selected card (click-to-expand) =====
    private string? _selectedStepId;
    private const double SelectedCardWidth = 580;

    private double WidthForStep(VideoStudio.Models.PipelineStep step) =>
        step.StepId == _selectedStepId ? SelectedCardWidth : 0; // 0 → GraphLayout uses ColumnWidth

    private void OnStepCardSelectionRequested(object? sender, EventArgs e)
    {
        if (sender is not PipelineStepCard card || card.Step == null) return;
        var newId = card.Step.StepId;
        if (_selectedStepId == newId) return;
        _selectedStepId = newId;
        foreach (var c in StepsPanel.Children.OfType<PipelineStepCard>())
        {
            c.IsCardSelected = c.Step?.StepId == _selectedStepId;
        }
        AnimateLayoutTransition();
    }

    private bool _isAnimatingLayout;

    private void AnimateLayoutTransition()
    {
        if (StepsPanel.Children.Count == 0) return;
        var layout = VideoStudio.Services.GraphLayout.Build(
            PipelineVM.MediaFiles,
            PipelineVM.Steps,
            MeasuredHeightFor,
            WidthForStep);

        // Remove wires; they'll be redrawn at the target positions when animation completes.
        var paths = StepsPanel.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().ToList();
        foreach (var p in paths) StepsPanel.Children.Remove(p);

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(380);

        foreach (var child in StepsPanel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            string? nodeId = fe.Tag switch
            {
                VideoStudio.Models.PipelineStep st => st.StepId,
                string s when s == VideoStudio.ViewModels.PipelineViewModel.BucketStepId => s,
                _ => null
            };
            if (nodeId == null) continue;
            var node = layout.FindByNodeId(nodeId);
            if (node == null) continue;

            // Capture current visual state as the From value — without this, animations
            // from NaN (unset Width) or unset Canvas attached properties snap instantly.
            var fromLeft = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(fe);
            if (double.IsNaN(fromLeft)) fromLeft = node.X;
            var fromTop = Microsoft.UI.Xaml.Controls.Canvas.GetTop(fe);
            if (double.IsNaN(fromTop)) fromTop = node.Y;

            AddDoubleAnim(sb, fe, "(Canvas.Left)", fromLeft, node.X, duration, ease);
            AddDoubleAnim(sb, fe, "(Canvas.Top)", fromTop, node.Y, duration, ease);
            if (fe is PipelineStepCard)
            {
                var fromWidth = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
                if (fromWidth <= 0) fromWidth = node.Width;
                AddDoubleAnim(sb, fe, "Width", fromWidth, node.Width, duration, ease);
            }
        }

        _isAnimatingLayout = true;
        _layoutDirtyDuringAnimation = false;

        // Live-redraw wires every frame while animation is running so they follow the cards
        // instead of vanishing and snapping back at the end.
        EventHandler<object>? rendering = null;
        rendering = (_, _) => RedrawWiresLive();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;

        sb.Completed += (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            foreach (var child in StepsPanel.Children)
            {
                if (child is not FrameworkElement fe) continue;
                string? nodeId = fe.Tag switch
                {
                    VideoStudio.Models.PipelineStep st => st.StepId,
                    string s when s == VideoStudio.ViewModels.PipelineViewModel.BucketStepId => s,
                    _ => null
                };
                if (nodeId == null) continue;
                var node = layout.FindByNodeId(nodeId);
                if (node == null) continue;
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(fe, node.X);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(fe, node.Y);
                if (fe is PipelineStepCard) fe.Width = node.Width;
            }
            _isAnimatingLayout = false;
            // If any card resized during the animation (logs streamed, a step completed,
            // artifacts appeared), the SizeChanged handler will have set this flag instead
            // of repacking mid-animation. Re-pack now so the final positions account for
            // the new heights and cards don't overlap.
            if (_layoutDirtyDuringAnimation)
            {
                _layoutDirtyDuringAnimation = false;
                RepackCanvas();
            }
            // Bring the selected card into view so its expanded body fits in the viewport.
            ScrollSelectedCardIntoView(layout);
            // RedrawWiresLive already painted at the final positions on its last tick — no extra DrawWires call.
        };
        sb.Begin();

        // Expand the canvas immediately so the scroller can reveal the wider card.
        StepsPanel.Width = Math.Max(StepsPanel.Width, layout.TotalWidth);
        StepsPanel.Height = Math.Max(StepsPanel.Height, layout.TotalHeight);
    }

    /// <summary>
    /// After the layout animation settles, pan PipelineScroller so the currently
    /// selected (expanded) card fits in the viewport. Adds a small margin so the
    /// card isn't flush against the viewport edge.
    /// </summary>
    private void ScrollSelectedCardIntoView(VideoStudio.Services.GraphLayout.GraphLayoutResult layout)
    {
        if (_selectedStepId == null) return;
        var node = layout.FindByNodeId(_selectedStepId);
        if (node == null) return;

        // Prefer the live element bounds — the expanded card body can grow taller
        // than the layout's pre-measured height as artifacts/log content streams in.
        double cardX = node.X, cardY = node.Y;
        double cardW = node.Width, cardH = node.Height;
        foreach (var child in StepsPanel.Children)
        {
            if (child is PipelineStepCard pc && pc.Step?.StepId == _selectedStepId)
            {
                cardW = pc.ActualWidth > 0 ? pc.ActualWidth : cardW;
                cardH = pc.ActualHeight > 0 ? pc.ActualHeight : cardH;
                break;
            }
        }

        const double margin = 24;
        double viewportW = PipelineScroller.ViewportWidth;
        double viewportH = PipelineScroller.ViewportHeight;
        if (viewportW <= 0 || viewportH <= 0) return;

        double currentH = PipelineScroller.HorizontalOffset;
        double currentV = PipelineScroller.VerticalOffset;
        double targetH = currentH;
        double targetV = currentV;

        double cardLeft = cardX - margin;
        double cardRight = cardX + cardW + margin;
        if (cardRight > currentH + viewportW) targetH = cardRight - viewportW;
        if (cardLeft < targetH) targetH = cardLeft;

        double cardTop = cardY - margin;
        double cardBottom = cardY + cardH + margin;
        if (cardBottom > currentV + viewportH) targetV = cardBottom - viewportH;
        if (cardTop < targetV) targetV = cardTop;

        if (Math.Abs(targetH - currentH) > 0.5 || Math.Abs(targetV - currentV) > 0.5)
            PipelineScroller.ChangeView(targetH, targetV, null);
    }

    private void RedrawWiresLive()
    {
        // Read current animated position from each card on the canvas, then redraw all wires.
        var positions = new System.Collections.Generic.Dictionary<string, (double X, double Y, double W, double H)>();
        foreach (var child in StepsPanel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            string? nodeId = fe.Tag switch
            {
                VideoStudio.Models.PipelineStep st => st.StepId,
                string s when s == VideoStudio.ViewModels.PipelineViewModel.BucketStepId => s,
                _ => null
            };
            if (nodeId == null) continue;
            var x = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(fe);
            var y = Microsoft.UI.Xaml.Controls.Canvas.GetTop(fe);
            if (double.IsNaN(x) || double.IsNaN(y)) continue;
            var w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
            var h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
            if (double.IsNaN(w) || double.IsNaN(h)) continue;
            positions[nodeId] = (x, y, w, h);
        }

        var paths = StepsPanel.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().ToList();
        foreach (var p in paths) StepsPanel.Children.Remove(p);

        foreach (var step in PipelineVM.Steps)
        {
            if (!positions.TryGetValue(step.StepId, out var consumer)) continue;
            foreach (var kv in step.Inputs)
            {
                var refStepId = kv.Value?.StepId;
                if (string.IsNullOrEmpty(refStepId)) continue;
                if (!positions.TryGetValue(refStepId, out var producer)) continue;

                var x1 = producer.X + producer.W;
                var y1 = producer.Y + producer.H / 2;
                var x2 = consumer.X;
                var y2 = consumer.Y + consumer.H / 2;
                var dx = Math.Max(40, (x2 - x1) / 2);

                var fig = new Microsoft.UI.Xaml.Media.PathFigure
                {
                    StartPoint = new Windows.Foundation.Point(x1, y1),
                    IsClosed = false,
                };
                fig.Segments.Add(new Microsoft.UI.Xaml.Media.BezierSegment
                {
                    Point1 = new Windows.Foundation.Point(x1 + dx, y1),
                    Point2 = new Windows.Foundation.Point(x2 - dx, y2),
                    Point3 = new Windows.Foundation.Point(x2, y2),
                });
                var geo = new Microsoft.UI.Xaml.Media.PathGeometry();
                geo.Figures.Add(fig);

                var path = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geo,
                    Stroke = WireBrushFor(kv.Value!.Kind),
                    StrokeThickness = 2,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(path, -1);
                StepsPanel.Children.Insert(0, path);
            }
        }
    }

    private static void AddDoubleAnim(Storyboard sb, DependencyObject target, string property, double from, double to, TimeSpan duration, EasingFunctionBase ease)
    {
        var anim = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }

    private void OnStepCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not PipelineStepCard card || card.Tag is not VideoStudio.Models.PipelineStep step) return;
        // Only rebuild if the height materially changed (avoid feedback loops on sub-pixel drift).
        var prev = _stepCardActualHeight.TryGetValue(step, out var p) ? p : 0;
        if (System.Math.Abs(prev - e.NewSize.Height) < 1.0) return;
        _stepCardActualHeight[step] = e.NewSize.Height;
        // While a layout animation is running, don't snap positions — but mark the layout
        // as dirty so the animation Completed handler re-packs after it finishes. Without
        // this, a card that grows during the animation (e.g., logs streaming in or a step
        // completing while another is being selected) would leave siblings overlapping it.
        if (_isAnimatingLayout)
        {
            _layoutDirtyDuringAnimation = true;
            return;
        }
        // Re-position siblings without re-creating cards (cheap).
        RepackCanvas();
    }

    private void RepackCanvas()
    {
        if (StepsPanel.Children.Count == 0) return;
        var layout = VideoStudio.Services.GraphLayout.Build(
            PipelineVM.MediaFiles,
            PipelineVM.Steps,
            MeasuredHeightFor,
            WidthForStep);

        // Remove old wires (Path elements) — cards stay in place.
        var paths = StepsPanel.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().ToList();
        foreach (var p in paths) StepsPanel.Children.Remove(p);

        foreach (var child in StepsPanel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            string? nodeId = fe.Tag switch
            {
                VideoStudio.Models.PipelineStep st => st.StepId,
                string s when s == VideoStudio.ViewModels.PipelineViewModel.BucketStepId => s,
                _ => null
            };
            if (nodeId == null) continue;
            var node = layout.FindByNodeId(nodeId);
            if (node == null) continue;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(fe, node.X);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(fe, node.Y);
        }

        DrawWires(layout);

        StepsPanel.Width = layout.TotalWidth;
        StepsPanel.Height = layout.TotalHeight;
    }

    /// <summary>Phase 8.10 — single bucket node listing all imported media files.
    /// Has Add buttons + per-file × buttons. Becomes a drop target for files dragged from the sidebar.</summary>
    private FrameworkElement BuildBucketNode(VideoStudio.Services.GraphLayout.GraphNode node)
    {
        var teal = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentTealBrush"];
        var border = new Border
        {
            Width = node.Width,
            Height = node.Height,
            CornerRadius = new CornerRadius(8),
            BorderBrush = teal,
            BorderThickness = new Thickness(2),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x0E, 0x8A, 0x7E)),
            Padding = new Thickness(12),
            Tag = VideoStudio.ViewModels.PipelineViewModel.BucketStepId,
            AllowDrop = true,
        };
        border.DragOver += BucketBorder_DragOver;
        border.Drop += BucketBorder_Drop;

        var grid = new Grid { RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // file list
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer buttons

        // Header
        var header = new TextBlock
        {
            Text = $"📦 INPUT ({PipelineVM.MediaFiles.Count})",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = teal,
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // File list (each row = filename + × button)
        var filesList = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var filesStack = new StackPanel { Spacing = 2 };
        if (PipelineVM.MediaFiles.Count == 0)
        {
            filesStack.Children.Add(new TextBlock
            {
                Text = "Drag files here or click + Add",
                FontSize = 11,
                Opacity = 0.6,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
        }
        foreach (var mf in PipelineVM.MediaFiles)
        {
            filesStack.Children.Add(BuildBucketFileRow(mf));
        }
        filesList.Content = filesStack;
        Grid.SetRow(filesList, 1);
        grid.Children.Add(filesList);

        // Footer buttons
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addBtn = new Button
        {
            Content = "+ Add files",
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
        };
        addBtn.Click += (_, _) => ImportButton_Click(addBtn, new RoutedEventArgs());
        footer.Children.Add(addBtn);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        border.Child = grid;
        return border;
    }

    private FrameworkElement BuildBucketFileRow(VideoStudio.Models.MediaFile mf)
    {
        var grid = new Grid
        {
            Padding = new Thickness(2),
            Margin = new Thickness(0, 2, 0, 2),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Thumbnail container — 80x45 (16:9), rounded, dark background placeholder
        var thumbBorder = new Border
        {
            Width = 80,
            Height = 45,
            CornerRadius = new CornerRadius(4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 32, 32, 36)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var ext = (mf.FileType ?? string.Empty).ToUpperInvariant();
        bool isVideo = ext is "MP4" or "MOV" or "MKV" or "AVI" or "WEBM" or "M4V";
        bool isAudio = ext is "WAV" or "MP3" or "M4A" or "FLAC" or "OGG" or "AAC";

        var placeholder = new TextBlock
        {
            Text = isVideo ? "🎬" : isAudio ? "🎵" : "📄",
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(180, 255, 255, 255)),
        };
        thumbBorder.Child = placeholder;
        Grid.SetColumn(thumbBorder, 0);
        grid.Children.Add(thumbBorder);

        if (isVideo)
        {
            _ = LoadBucketThumbAsync(mf.FilePath, thumbBorder);
        }

        // Metadata stack — name + (duration · type)
        var metaStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            Spacing = 1,
        };
        var nameTb = new TextBlock
        {
            Text = mf.FileName,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        ToolTipService.SetToolTip(nameTb, mf.FilePath);
        var subTb = new TextBlock
        {
            Text = $"{mf.Duration}  ·  {ext}",
            FontSize = 10,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        metaStack.Children.Add(nameTb);
        metaStack.Children.Add(subTb);
        Grid.SetColumn(metaStack, 1);
        grid.Children.Add(metaStack);

        var rmBtn = new Button
        {
            Content = "×",
            FontSize = 12,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(rmBtn, "Remove from input bucket");
        rmBtn.Click += (_, _) =>
        {
            PipelineVM.MediaFiles.Remove(mf);
        };
        Grid.SetColumn(rmBtn, 2);
        grid.Children.Add(rmBtn);

        return grid;
    }

    private async System.Threading.Tasks.Task LoadBucketThumbAsync(string filePath, Border target)
    {
        try
        {
            var bmp = await VideoStudio.Services.ThumbnailService.ExtractSingleThumbnailAsync(
                filePath, timeOffset: null, width: 160, height: 90);
            if (bmp == null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                var img = new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = bmp,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                };
                target.Child = img;
            });
        }
        catch
        {
            // leave placeholder on error
        }
    }

    private void BucketBorder_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to input bucket";
        }
    }

    private async void BucketBorder_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.StorageFile>().Select(sf => sf.Path).ToArray();
            if (paths.Length > 0)
            {
                PipelineVM.ImportFilesCommand.Execute(paths);
            }
        }
    }


    /// <summary>
    /// Returns valid upstream producers for the given (consumer step, input pin).
    /// Excludes the consumer itself and any reachable-downstream nodes (cycle prevention).
    /// Matches by ArtifactKind on declared OutputPins. Includes source video as a candidate
    /// for Video-kind pins.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<VideoStudio.Controls.PipelineStepCard.PinCandidate> GetCandidatesFor(
        VideoStudio.Models.PipelineStep consumer,
        VideoStudio.Models.EffectPin pin)
    {
        var results = new System.Collections.Generic.List<VideoStudio.Controls.PipelineStepCard.PinCandidate>();

        // Phase 8.10 — single bucket as candidate for Video / Audio pins.
        if (pin.Kind == VideoStudio.Models.ArtifactKind.Video || pin.Kind == VideoStudio.Models.ArtifactKind.Audio)
        {
            results.Add(new VideoStudio.Controls.PipelineStepCard.PinCandidate(
                ProducerStepId: VideoStudio.ViewModels.PipelineViewModel.BucketStepId,
                ProducerName: $"Input bucket ({PipelineVM.MediaFiles.Count} files)",
                ProducerPinId: "video",
                Kind: pin.Kind));
        }

        // Reachable-downstream from consumer (so we exclude cycles).
        var downstream = ComputeDownstream(consumer);

        foreach (var step in PipelineVM.Steps)
        {
            if (ReferenceEquals(step, consumer)) continue;
            if (downstream.Contains(step.StepId)) continue;
            var effect = PipelineVM.AvailableEffects.FirstOrDefault(e => e.Id == step.Id);
            if (effect == null) continue;
            foreach (var outPin in effect.EffectiveOutputPins)
            {
                if (outPin.Kind != pin.Kind) continue;
                results.Add(new VideoStudio.Controls.PipelineStepCard.PinCandidate(
                    ProducerStepId: step.StepId,
                    ProducerName: step.Name,
                    ProducerPinId: outPin.Id,
                    Kind: outPin.Kind));
            }
        }
        return results;
    }

    private System.Collections.Generic.HashSet<string> ComputeDownstream(VideoStudio.Models.PipelineStep root)
    {
        var down = new System.Collections.Generic.HashSet<string>();
        var stack = new System.Collections.Generic.Stack<string>();
        stack.Push(root.StepId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var step in PipelineVM.Steps)
            {
                if (down.Contains(step.StepId)) continue;
                foreach (var kv in step.Inputs)
                {
                    if (kv.Value?.StepId == cur)
                    {
                        if (down.Add(step.StepId))
                            stack.Push(step.StepId);
                        break;
                    }
                }
            }
        }
        return down;
    }

    private void ApplyPinSelection(VideoStudio.Models.PipelineStep step, string pinId, VideoStudio.Models.ArtifactRef? newRef)
    {
        if (newRef == null)
        {
            step.Inputs.Remove(pinId);
        }
        else
        {
            step.Inputs[pinId] = newRef;
        }
        // Re-layout & re-draw wires.
        RebuildPipelineUI();
    }



    private void OnHardwareHistoryUpdated()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            CpuSparkline.Points = _hardwareMonitor.CpuHistory.ToList();
            GpuSparkline.Points = _hardwareMonitor.GpuHistory.ToList();
            NpuSparkline.Points = _hardwareMonitor.NpuHistory.ToList();

            CpuValueText.Text = $"{_hardwareMonitor.CpuPercent:F0}%";
            GpuValueText.Text = $"{_hardwareMonitor.GpuPercent:F0}%";
            NpuValueText.Text = $"{_hardwareMonitor.NpuPercent:F0}%";
            MemValueText.Text = $"{_hardwareMonitor.MemoryUsedMB:F0} MB";
        });
    }

    // ── Sidebar ──────────────────────────────────────────────────────

    private void PopulateSidebar()
    {
        foreach (var effect in PipelineVM.ContainerEffects)
            ContainerEffectsPanel.Children.Add(CreateSidebarEffectCard(effect));
        foreach (var effect in PipelineVM.NativeEffects)
            NativeEffectsPanel.Children.Add(CreateSidebarEffectCard(effect));

        UpdateSourcesList();
    }

    private UIElement CreateSidebarEffectCard(Effect effect)
    {
        var icon = new FontIcon
        {
            Glyph = effect.Icon,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var name = new TextBlock
        {
            Text = effect.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var desc = new TextBlock
        {
            Text = effect.Description,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            MaxLines = 2,
        };
        var textStack = new StackPanel { Spacing = 1 };
        textStack.Children.Add(name);
        textStack.Children.Add(desc);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(icon);
        content.Children.Add(textStack);

        // Use Border instead of Button so CanDrag works (Button captures pointer, blocking drag)
        var card = new Border
        {
            Child = content,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Tag = effect,
            CanDrag = true,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, $"effectcard-{effect.Id}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"Add {effect.Name}");
        card.DragStarting += EffectCard_DragStarting;
        card.Tapped += SidebarEffectCard_Tapped;
        card.PointerEntered += (s, _) => card.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        card.PointerExited += (s, _) => card.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

        // Invisible InvokePattern shim button so UIA tests / screen readers can activate the
        // card without simulating a drag. Hit-test off so it doesn't intercept pointer drag.
        var invokeShim = new Button
        {
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = effect,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(invokeShim, $"addeffect-{effect.Id}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(invokeShim, $"Add {effect.Name}");
        invokeShim.Click += (s, _) => PipelineVM.AddStepCommand.Execute(effect);

        var wrapper = new Grid();
        wrapper.Children.Add(card);
        wrapper.Children.Add(invokeShim);
        return wrapper;
    }

    private void SidebarEffectCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: Effect effect })
        {
            PipelineVM.AddStepCommand.Execute(effect);
        }
    }

    // ── Drag and drop ────────────────────────────────────────────────

    // Protocol: DataPackage text = "effect:{id}" | "source:{path}" | "step:{id}"

    private void EffectCard_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        Effect? effect = null;
        if (sender is Border { Tag: Effect e }) effect = e;
        else if (sender is Button { Tag: Effect e2 }) effect = e2;

        if (effect != null)
        {
            args.Data.SetText($"effect:{effect.Id}");
            args.Data.RequestedOperation = DataPackageOperation.Copy;
        }
    }

    private void SourceItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is ListViewItem { Tag: MediaFile file })
        {
            args.Data.SetText($"source:{file.FilePath}");
            args.Data.RequestedOperation = DataPackageOperation.Link;
        }
    }

    private void SourcesListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is ListViewItem { Tag: MediaFile file })
        {
            e.Data.SetText($"source:{file.FilePath}");
            e.Data.RequestedOperation = DataPackageOperation.Link;
        }
    }

    private void StepCard_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is PipelineStepCard card && card.Step != null)
        {
            args.Data.SetText($"step:{card.Step.Id}");
            args.Data.RequestedOperation = DataPackageOperation.Move;
        }
    }

    private void PipelineCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Add to pipeline";
        }
    }

    private async void PipelineCanvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var payload = await e.DataView.GetTextAsync();
        HandleDrop(payload, -1); // -1 = append
    }

    private void AddStepButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Insert here";
            // Highlight the add button
            if (sender is AddStepButton btn)
                btn.Opacity = 1.0;
        }
    }

    private void AddStepButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is AddStepButton btn)
            btn.Opacity = 0.7;
    }

    private async void AddStepButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is AddStepButton btn)
            btn.Opacity = 0.7;

        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var payload = await e.DataView.GetTextAsync();

        // Determine insert index from the AddStepButton's position in StepsPanel
        int insertIndex = -1;
        if (sender is AddStepButton addBtn)
        {
            // Phase 8.9 — InitialAddButton removed from XAML; AddStepButtons now only come from sidebar effect cards.
            for (int i = 0; i < StepsPanel.Children.Count; i++)
            {
                if (StepsPanel.Children[i] == addBtn)
                {
                    insertIndex = (i / 2) + 1;
                    break;
                }
            }
        }

        HandleDrop(payload, insertIndex);
    }

    private void StepCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "Reorder";
        }
    }

    private async void StepCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var payload = await e.DataView.GetTextAsync();

        if (payload.StartsWith("step:") && sender is PipelineStepCard targetCard && targetCard.Step != null)
        {
            var draggedId = payload["step:".Length..];
            var targetIdx = PipelineVM.Steps.IndexOf(targetCard.Step);
            HandleDrop(payload, targetIdx);
        }
    }

    private void HandleDrop(string payload, int insertIndex)
    {
        if (payload.StartsWith("effect:"))
        {
            var effectId = payload["effect:".Length..];
            var effect = PipelineVM.AvailableEffects.FirstOrDefault(e => e.Id == effectId);
            if (effect != null)
            {
                if (insertIndex >= 0)
                    PipelineVM.InsertStep(insertIndex - 1, effect);
                else
                    PipelineVM.AddStepCommand.Execute(effect);
            }
        }
        else if (payload.StartsWith("source:"))
        {
            var path = payload["source:".Length..];
            var file = PipelineVM.MediaFiles.FirstOrDefault(f => f.FilePath == path);
            if (file != null)
                PipelineVM.SourceFile = file;
        }
        else if (payload.StartsWith("step:"))
        {
            var stepId = payload["step:".Length..];
            var step = PipelineVM.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step != null && insertIndex >= 0)
            {
                var currentIdx = PipelineVM.Steps.IndexOf(step);
                if (currentIdx != insertIndex && currentIdx >= 0)
                {
                    PipelineVM.Steps.Move(currentIdx, Math.Min(insertIndex, PipelineVM.Steps.Count - 1));
                }
            }
        }
    }

    // ── Sources list management ──────────────────────────────────────

    private void OnMediaFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSourcesList();
        // Phase 8.9 — sources are now nodes on the canvas; rebuild so they appear as layer-0 cards.
        RebuildPipelineUI();
    }

    private void UpdateSourcesList()
    {
        SourcesListView.Items.Clear();
        foreach (var file in PipelineVM.MediaFiles)
        {
            var item = new ListViewItem
            {
                Tag = file,
                CanDrag = true,
            };
            item.DragStarting += SourceItem_DragStarting;

            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new TextBlock
            {
                Text = file.FileSize,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            item.Content = stack;
            SourcesListView.Items.Add(item);
        }
        NoSourcesText.Visibility = PipelineVM.MediaFiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateSourcesListSelection();
    }

    private void UpdateSourcesListSelection()
    {
        if (PipelineVM.SourceFile != null)
        {
            for (int i = 0; i < SourcesListView.Items.Count; i++)
            {
                if (SourcesListView.Items[i] is ListViewItem li && li.Tag == PipelineVM.SourceFile)
                {
                    SourcesListView.SelectedIndex = i;
                    return;
                }
            }
        }
        SourcesListView.SelectedIndex = -1;
    }

    private void SourcesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourcesListView.SelectedItem is ListViewItem { Tag: MediaFile file })
        {
            if (PipelineVM.SourceFile != file)
                PipelineVM.SourceFile = file;
        }
    }

    // ── Command bar handlers ─────────────────────────────────────────

    private void RunAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (PipelineVM.RunAllCommand.CanExecute(null))
            PipelineVM.RunAllCommand.Execute(null);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        PipelineVM.StopExecutionCommand.Execute(null);
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4"); picker.FileTypeFilter.Add(".mkv"); picker.FileTypeFilter.Add(".avi"); picker.FileTypeFilter.Add(".mov"); picker.FileTypeFilter.Add(".wmv"); picker.FileTypeFilter.Add(".webm"); picker.FileTypeFilter.Add(".wav"); picker.FileTypeFilter.Add(".mp3"); picker.FileTypeFilter.Add(".m4a"); picker.FileTypeFilter.Add(".flac"); picker.FileTypeFilter.Add(".ogg"); picker.FileTypeFilter.Add(".aac");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            PipelineVM.ImportFilesCommand.Execute(files.Select(f => f.Path).ToArray());
        }
    }

    private void SidebarImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportButton_Click(sender, e);
    }

    // ── Batch overlay (Phase 8.7) ─────────────────────────────────────

    private void BatchButton_Click(object sender, RoutedEventArgs e)
    {
        BatchOverlay.Visibility = Visibility.Visible;
    }

    private void BatchClose_Click(object sender, RoutedEventArgs e)
    {
        BatchOverlay.Visibility = Visibility.Collapsed;
    }

    private async void BatchAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm",
                                    ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".aac" })
            picker.FileTypeFilter.Add(ext);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;

        foreach (var f in files)
        {
            var media = new VideoStudio.Models.MediaFile(f.Path);
            PipelineVM.AddBatchSourceCommand.Execute(media);
        }
    }

    private void BatchClear_Click(object sender, RoutedEventArgs e)
    {
        PipelineVM.ClearBatchSourcesCommand.Execute(null);
    }

    private async void BatchRun_Click(object sender, RoutedEventArgs e)
    {
        if (PipelineVM.BatchSources.Count == 0)
        {
            BatchStatusText.Text = "Add at least one file first.";
            return;
        }
        if (PipelineVM.Steps.Count == 0)
        {
            BatchStatusText.Text = "Build a notebook in the canvas first.";
            return;
        }

        BatchProgressRing.IsActive = true;
        BatchRunButton.IsEnabled = false;
        BatchStatusText.Text = $"Running {PipelineVM.BatchSources.Count} files…";
        try
        {
            await PipelineVM.RunBatchCommand.ExecuteAsync(null);
            BatchStatusText.Text = $"Done — {PipelineVM.BatchResults.Count(r => r.Success)}/{PipelineVM.BatchResults.Count} succeeded.";
        }
        finally
        {
            BatchProgressRing.IsActive = false;
            BatchRunButton.IsEnabled = true;
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOutputFolder();
    }

    // ── Notebook (open / save / templates) ────────────────────────────

    private void PopulateNotebookTemplates()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Notebooks");
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var path in System.IO.Directory.GetFiles(dir, "*" + NotebookSerializer.FileExtension))
            {
                NotebookDocument? doc = null;
                try { doc = NotebookSerializer.Load(path); } catch { continue; }
                if (doc == null) continue;
                var item = new MenuFlyoutItem
                {
                    Text = doc.Name,
                    Tag = path,
                };
                if (!string.IsNullOrEmpty(doc.Description))
                    ToolTipService.SetToolTip(item, doc.Description);
                item.Icon = new FontIcon { Glyph = "\uE7C3" };
                item.Click += NotebookTemplate_Click;
                NotebookTemplatesMenu.Items.Add(item);
            }
        }
        catch
        {
            // Templates are best-effort.
        }
    }

    private void NotebookTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem mfi && mfi.Tag is string path)
        {
            try
            {
                var doc = NotebookSerializer.Load(path);
                int loaded = NotebookSerializer.ApplyTo(doc, PipelineVM);
                StatusBarText.Text = $"Loaded template: {doc.Name} ({loaded} steps)";
            }
            catch (Exception ex)
            {
                StatusBarText.Text = $"Failed to load template: {ex.Message}";
            }
        }
    }

    private void NotebookNew_Click(object sender, RoutedEventArgs e)
    {
        PipelineVM.ClearSteps();
        StatusBarText.Text = "New notebook (cleared)";
    }

    private async void NotebookOpen_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(NotebookSerializer.FileExtension);
        picker.FileTypeFilter.Add(".json");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        try
        {
            var doc = NotebookSerializer.Load(file.Path);
            int loaded = NotebookSerializer.ApplyTo(doc, PipelineVM);
            StatusBarText.Text = $"Opened {doc.Name} ({loaded} steps)";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"Failed to open notebook: {ex.Message}";
        }
    }

    private async void NotebookSave_Click(object sender, RoutedEventArgs e)
    {
        if (PipelineVM.Steps.Count == 0)
        {
            StatusBarText.Text = "Nothing to save — pipeline is empty";
            return;
        }
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("Contoso Studio Notebook", new[] { NotebookSerializer.FileExtension });
        picker.SuggestedFileName = "MyNotebook";
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            var doc = NotebookSerializer.FromSteps(
                PipelineVM.Steps,
                name: System.IO.Path.GetFileNameWithoutExtension(file.Name));
            NotebookSerializer.Save(file.Path, doc);
            StatusBarText.Text = $"Saved notebook: {file.Name} ({doc.Steps.Count} steps)";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"Failed to save notebook: {ex.Message}";
        }
    }

    // ── Source / Output card event handlers ───────────────────────────

    private async void SourceCard_ChangeRequested(object? sender, EventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4"); picker.FileTypeFilter.Add(".mkv"); picker.FileTypeFilter.Add(".avi"); picker.FileTypeFilter.Add(".mov"); picker.FileTypeFilter.Add(".wmv"); picker.FileTypeFilter.Add(".webm"); picker.FileTypeFilter.Add(".wav"); picker.FileTypeFilter.Add(".mp3"); picker.FileTypeFilter.Add(".m4a"); picker.FileTypeFilter.Add(".flac"); picker.FileTypeFilter.Add(".ogg"); picker.FileTypeFilter.Add(".aac");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            PipelineVM.ImportFilesCommand.Execute(new[] { file.Path });
        }
    }

    private void OutputCard_OpenFolderRequested(object? sender, EventArgs e)
    {
        OpenOutputFolder();
    }

    private static void OpenOutputFolder()
    {
        var outputDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ContosoStudio");
        if (System.IO.Directory.Exists(outputDir))
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(outputDir) { UseShellExecute = true });
        }
    }

    // ── Add step handlers ────────────────────────────────────────────

    private void InitialAddButton_EffectSelected(object? sender, Effect effect)
    {
        PipelineVM.AddStepCommand.Execute(effect);
    }

    // (InfoBar removed — Closed handler no longer needed)
}
