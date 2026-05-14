using System.Collections.Generic;

namespace VideoStudio.Models;

/// <summary>
/// Strongly-typed artifact kinds that can flow between pipeline steps.
/// Used for pin metadata on Effect, type-checked refs on Step, and color-coding in UI.
/// Phase 8.1 — see plan.md.
/// </summary>
public enum ArtifactKind
{
    /// <summary>An MP4 / MOV file on disk (visual content). Default for nearly every step.</summary>
    Video,
    /// <summary>A WAV/MP3/AAC audio-only file (e.g. Extract Audio output, Whisper input).</summary>
    Audio,
    /// <summary>Whisper transcript with segments + optional word timestamps.</summary>
    Transcript,
    /// <summary>Per-frame YOLOS / detection bounding boxes + labels.</summary>
    Detections,
    /// <summary>Per-frame normalized depth maps from Depth-Anything.</summary>
    Depth,
    /// <summary>Per-frame top-K ImageNet/CLIP labels.</summary>
    SceneTags,
    /// <summary>Time ranges of "interesting moments" (Highlight Picker output).</summary>
    Highlights,
    /// <summary>Time ranges of silence (Detect Silence output, consumed by Smart Cut).</summary>
    Silence,
    /// <summary>Chapter boundaries with titles (Phi Silica chapter markers).</summary>
    Chapters,
    /// <summary>Free-form text — show notes, captions burned-in summary, etc.</summary>
    Text
}

/// <summary>
/// Base class for any artifact a step can produce or consume.
/// Concrete subclasses carry kind-specific data; <see cref="Kind"/> drives type checks.
/// </summary>
public abstract class Artifact
{
    public abstract ArtifactKind Kind { get; }
    /// <summary>Unique id of the producing step (for traceability / cache invalidation).</summary>
    public string ProducingStepId { get; init; } = string.Empty;
    /// <summary>Pin id on the producing step (e.g. "video", "transcript").</summary>
    public string ProducingPinId { get; init; } = string.Empty;
    /// <summary>Optional path on disk for file-based artifacts.</summary>
    public string? Path { get; init; }
}

public sealed class VideoArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Video;
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
}

public sealed class AudioArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Audio;
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}

public sealed class TranscriptArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Transcript;
    public string FullText { get; init; } = string.Empty;
    public string Language { get; init; } = "en";
    /// <summary>List of (startMs, endMs, text). Optional but recommended.</summary>
    public IReadOnlyList<(int startMs, int endMs, string text)> Segments { get; init; } = System.Array.Empty<(int, int, string)>();
}

public sealed class DetectionsArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Detections;
    public int FrameCount { get; init; }
    /// <summary>Total detections across all frames (for summary line).</summary>
    public int TotalCount { get; init; }
    /// <summary>Distinct labels seen.</summary>
    public IReadOnlyList<string> Labels { get; init; } = System.Array.Empty<string>();
}

public sealed class DepthArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Depth;
    public int FrameCount { get; init; }
}

public sealed class SceneTagsArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.SceneTags;
    public int FrameCount { get; init; }
    public IReadOnlyList<string> TopLabels { get; init; } = System.Array.Empty<string>();
}

public sealed class HighlightsArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Highlights;
    public IReadOnlyList<(double startSec, double endSec, string reason)> Ranges { get; init; }
        = System.Array.Empty<(double, double, string)>();
}

public sealed class SilenceArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Silence;
    public IReadOnlyList<(double startSec, double endSec)> Ranges { get; init; }
        = System.Array.Empty<(double, double)>();
}

public sealed class ChaptersArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Chapters;
    public IReadOnlyList<(double startSec, string title)> Chapters { get; init; }
        = System.Array.Empty<(double, string)>();
}

public sealed class TextArtifact : Artifact
{
    public override ArtifactKind Kind => ArtifactKind.Text;
    public string Content { get; init; } = string.Empty;
    public string? Title { get; init; }
}

/// <summary>
/// Reference to an upstream step's output pin. Used by <see cref="PipelineStep.Inputs"/>
/// to wire fan-in. <c>null</c> => use the implicit "nearest prior matching artifact"
/// fallback resolved by GetInput&lt;T&gt;.
/// </summary>
public sealed class ArtifactRef
{
    public string StepId { get; init; } = string.Empty;
    public string PinId { get; init; } = string.Empty;
    public ArtifactKind Kind { get; init; }
}
