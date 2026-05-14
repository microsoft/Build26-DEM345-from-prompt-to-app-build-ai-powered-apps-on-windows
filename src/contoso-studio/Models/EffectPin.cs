namespace VideoStudio.Models;

/// <summary>
/// Declares a single input or output slot on an effect.
/// Drives type-checked connections, UI dropdowns for picking sources, and color-coded pins.
/// Phase 8.1 — see plan.md.
/// </summary>
public sealed class EffectPin
{
    /// <summary>Stable id used in <see cref="PipelineStep.Inputs"/> / <see cref="PipelineStep.Outputs"/> dictionaries.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Human-readable label for the UI (e.g. "Video", "Transcript", "Captions").</summary>
    public string Label { get; set; } = string.Empty;
    public ArtifactKind Kind { get; set; }
    /// <summary>If true, the step refuses to run until this input is connected (or the implicit fallback resolves).</summary>
    public bool Required { get; set; } = true;

    public EffectPin() { }
    public EffectPin(string id, string label, ArtifactKind kind, bool required = true)
    {
        Id = id; Label = label; Kind = kind; Required = required;
    }
}
