You are working on **Contoso Studio**, a WinUI 3 demo app showcasing the WSL Container SDK.

## Project Context

This is a demo/showcase app for Microsoft's unreleased WSL Container SDK (`wslc`). The goal is to show Linux containers running alongside native Windows AI in a professional desktop app. It's NOT a production app — it's a polished demo for presentations and developer evangelism.

## Tech Stack

- **WinUI 3** (C#, .NET 10, XAML) with MSIX packaging
- **CommunityToolkit.Mvvm** for MVVM with source generators
- **WSL Container SDK** via `wslc.exe` CLI (the P/Invoke layer in `WslcInterop.cs` exists but is not used — it crashes in WinUI apps)
- **Windows ML** via `Microsoft.WindowsAppSDK.ML` — YOLOS-small on NPU via ONNX Runtime + QNN EP
- **15+ WinUI 3 controls** showcased: NavigationView, CommandBar, InfoBar, Expander, ProgressRing, MenuFlyout, MediaPlayerElement, Canvas+Polyline sparklines, etc.

## Critical Build/Run Instructions

```powershell
# Build (must specify Platform)
dotnet build VideoStudio.csproj -p:Platform=x64 -c Debug

# Run — MUST use winapp or packaged launch, NEVER run the .exe directly
winapp run bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\
```

## Key Conventions

- **Partial properties**: `[ObservableProperty] public partial string Name { get; set; }` (not fields)
- **No x:Bind converters**: Window ≠ FrameworkElement in WinUI 3. Use inverse bool properties instead
- **Nullable bindings**: Never bind to `Foo.Bar` in XAML where `Foo` can be null — the app will crash. Push values from code-behind instead
- **Pipeline UI built in code-behind**: `RebuildPipelineUI()` creates PipelineStepCard + AddStepButton controls programmatically to avoid DataTemplate/x:Bind issues
- **Container effects**: Use `wslc.exe run --rm -v <path>:/data <image> <cmd>` — NOT the native P/Invoke SDK
- **UI thread updates**: Use `DispatcherQueue.TryEnqueue()` when updating from background threads
- **NuGet**: `Microsoft.WSL.Containers` is bundled in `packages/`, `Microsoft.WindowsAppSDK.ML` for Windows ML
- **Theme**: Teal accent `#0E8A7E`, container orange `#E87D2F`. Use `AccentTealBrush` / `ContainerBadgeBrush`
- **ONNX model**: Simplified with `onnxsim` for QNN EP compatibility (dynamic reshapes fail on NPU)

## Architecture Overview

```
MainWindow.xaml              → NavigationView + CommandBar + pipeline canvas + sparkline status bar
MainWindow.xaml.cs           → Pipeline UI rebuild, video import, sparkline updates
ViewModels/
  PipelineViewModel.cs       → Pipeline orchestration: steps, effects catalog, execution
Services/
  ContainerRunner.cs         → wslc.exe CLI wrapper
  OnnxModelRunner.cs         → YOLOS-small inference via Windows ML + NPU
  HardwareMonitor.cs         → 1Hz CPU/GPU/NPU/RAM polling with sparkline history
  ThumbnailService.cs        → Video frame extraction as WriteableBitmap
  SuperResolutionRunner.cs   → Windows AI placeholder
  WslcInterop.cs             → P/Invoke (REFERENCE ONLY)
Controls/
  PipelineStepCard.xaml      → Expander-based step card with inline playback + HW bars
  AddStepButton.xaml         → "+" button with MenuFlyout effect catalog
  SourceCard.xaml             → Source video card with inline MediaPlayerElement
  PipelineOutputCard.xaml    → Result card with playback
  HardwareUsageBars.xaml     → 4-bar CPU/GPU/NPU/RAM visualization
  SparklineChart.xaml        → Canvas+Polyline mini chart
Models/
  PipelineStep.cs            → Step: state, progress, I/O paths, HW usage, log
  Effect.cs                  → Effect definition with engine + dependencies
  MediaFile.cs               → Imported file metadata
  EngineType.cs              → LinuxContainer | WindowsML | WindowsAI
```

## What's Working

- ✅ Pipeline builder UI with NavigationView, CommandBar, InfoBar, Expander step cards
- ✅ Inline video playback via MediaPlayerElement in source/step/output cards
- ✅ Effects catalog page with clickable cards that add steps to pipeline
- ✅ "+" Add Step buttons with MenuFlyout between pipeline steps
- ✅ Hardware monitoring with sparkline charts in status bar
- ✅ Real ONNX inference on NPU (287 detections on test video, 97% confidence)
- ✅ Container effects via wslc.exe (Extract Audio end-to-end)
- ✅ Pipeline execution with step chaining and per-step HW recording
