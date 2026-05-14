# Contoso Studio

A WinUI 3 desktop **AI pipeline builder** for video — build a DAG of effects that run **Windows ML** (ONNX Runtime on NPU) and **Windows AI** (Phi Silica) over a batch of input videos, all in one native Windows app showcasing 15+ WinUI 3 controls.

## Prerequisites

- **Windows 11** (NPU strongly recommended — Snapdragon X / Copilot+ PC for Detect Objects, Phi Silica)
- **Developer Mode enabled** (Settings → System → For developers → On)
- **.NET 10 SDK** — targets `net10.0-windows10.0.26100.0`

## Quick Start

```powershell
# Clone, then from the repo root:
pwsh -File setup.ps1   # one-time: fetches tools/ffmpeg.exe (~100 MB)
dotnet run
```

That's it. `dotnet run` builds, packages, and launches the MSIX app via the `Microsoft.Windows.SDK.BuildTools.WinApp` build hooks — no separate `winapp run` step needed.

`setup.ps1` only fetches ffmpeg. **All ML models auto-download on first use** to `%LOCALAPPDATA%\Contoso Studio\models\`:

| Model | Size | Used by |
|-------|------|---------|
| `yolos-small.onnx` | ~110 MB | Detect Objects |
| `whisper_tiny_int8_cpu_ort_1.18.0.onnx` | ~77 MB | Transcribe (default) |
| `whisper_small_int8_cpu_ort_1.18.0.onnx` | ~244 MB | Transcribe (Small) |
| `whisper_medium_int8_cpu_ort_1.18.0.onnx` | ~768 MB | Transcribe (Medium) |
| `depth-anything-v2-small.onnx` | ~100 MB | Depth Map |
| `vit-base-patch16-224.onnx` | ~88 MB | Scene Tags |

Phi Silica (Chapter Markers, Show Notes, Highlights) uses the system Windows AI runtime — no manual download needed on Copilot+ PCs.

## Architecture

### Layout (LTR DAG pipeline builder)

```
┌──────────────────────────────────────────────────────────────────────┐
│  ◉ Contoso Studio                                       ─ □ ✕       │
├──────────────────────────────────────────────────────────────────────┤
│  CommandBar: [▶ Run All] [⏹ Stop] | [📂 Import] [📁 Output]         │
│  InfoBar: "✓ NPU detected — Qualcomm QNN EP"                         │
├────────────────────────────────────────────────────┬─────────────────┤
│  Pipeline Canvas — left-to-right DAG, layer 0 is   │  SOURCES (280)  │
│  the input bucket; effects auto-wire and fan out   │  ┌ talk.mp4 ✓ ┐ │
│  in parallel from the bucket.                      │  └ demo.mp4 ✓ ┘ │
│                                                    │  [+ Import]     │
│  ┌─ 📦 INPUT (2) ──┐ ─→ ┌─ Detect Objects 🧠 ─┐  │  ─────────────  │
│  │ 🎬 talk.mp4  × │    │ ▸ HW bars · log · …  │   │  EFFECTS        │
│  │ 🎬 demo.mp4  × │ ─→ ├─ Transcribe   🧠 ─┤   │  🧠 Detect Obj. │
│  │ + Add files     │    │ ▸ per-file results … │   │  🧠 Transcribe  │
│  └─────────────────┘ ─→ ├─ Show Notes  ✨ ─┤   │  ✨ Chapters    │
│                          │  (depends on transcript)│  ✨ Show Notes  │
│                          └──────────────────────┘  │  🪶 Smart Cut   │
│                                                    │  …              │
├────────────────────────────────────────────────────┴─────────────────┤
│ CPU ╱╲╱─ 18% │ GPU ─╱── 4% │ NPU ╱╲╱╲ 82% │ 228 MB                 │
└──────────────────────────────────────────────────────────────────────┘
```

- **Single input bucket** at layer 0 — drag/drop or click "+ Add files" to batch over N videos.
- **Effects fan out in parallel** by default (all wire to the bucket). Steps with declared dependencies (e.g. Show Notes ← Transcribe) auto-route from the upstream step.
- **Pin chips** on each step show the wired producer; click to retarget via dropdown.
- **Bezier wires** color-coded by artifact kind (Video=teal, Audio=orange, Transcript=blue).

### Effects Pipeline

| Effect | Engine | What it does |
|--------|--------|-------------|
| Detect Objects | 🧠 Windows ML | YOLOS-small on NPU via ONNX Runtime + QNN EP (real inference) |
| Transcribe | 🧠 Windows ML | Whisper int8 ONNX (tiny / small / medium) |
| Depth Map | 🧠 Windows ML | Depth-Anything-v2 small ONNX |
| Scene Tags | 🧠 Windows ML | ViT-base-patch16-224 ONNX classifier |
| Chapter Markers | ✨ Windows AI | Phi Silica generates chapter boundaries from transcript |
| Show Notes | ✨ Windows AI | Phi Silica summarizes transcript into markdown notes |
| Highlights | ✨ Windows AI | Phi Silica picks highlight clips from transcript |
| Detect Silence | 🪶 Native | RMS analysis on extracted audio |
| Smart Cut | 🪶 Native | Trims silence/low-motion segments via ffmpeg |
| Caption Burn-in | 🪶 Native | ffmpeg burns SRT onto video |
