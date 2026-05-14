using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoStudio.Models;

public partial class Effect : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial string Description { get; set; }
    [ObservableProperty] public partial EngineType Engine { get; set; }
    [ObservableProperty] public partial string Icon { get; set; }

    /// <summary>
    /// Effect IDs that must be applied before this one.
    /// </summary>
    public string[] Dependencies { get; set; } = [];

    /// <summary>
    /// Typed input pins this effect consumes (Phase 8.1). Empty = legacy single-video input
    /// is implied (the executor uses <see cref="PipelineStep.InputVideoPath"/> directly).
    /// </summary>
    public EffectPin[] InputPins { get; set; } = [];

    /// <summary>
    /// Typed output pins this effect produces (Phase 8.1). Empty = legacy single-video
    /// output is implied (the executor sets <see cref="PipelineStep.OutputVideoPath"/>).
    /// </summary>
    public EffectPin[] OutputPins { get; set; } = [];

    /// <summary>
    /// Phase 8.8.1: pins to use for layout/wiring/backfill. Returns the explicitly declared
    /// <see cref="InputPins"/> when present; otherwise returns a single default <c>video</c>
    /// pin so legacy effects still participate in the graph as a single-input chain.
    /// </summary>
    public EffectPin[] EffectiveInputPins => InputPins.Length > 0
        ? InputPins
        : [ new EffectPin { Id = "video", Label = "Video", Kind = ArtifactKind.Video, Required = true } ];

    /// <summary>
    /// Phase 8.8.1: same logic for outputs. Returns the explicitly declared
    /// <see cref="OutputPins"/> when present; otherwise a single default <c>video</c> pin.
    /// </summary>
    public EffectPin[] EffectiveOutputPins => OutputPins.Length > 0
        ? OutputPins
        : [ new EffectPin { Id = "video", Label = "Video", Kind = ArtifactKind.Video, Required = false } ];

    public string EngineBadge => Engine switch
    {
        EngineType.LinuxContainer => "Linux Container",
        EngineType.WindowsML => "Windows ML",
        EngineType.WindowsAI => "Windows AI",
        _ => "Unknown"
    };

    public Effect(string id, string name, string description, EngineType engine, string icon)
    {
        Id = id;
        Name = name;
        Description = description;
        Engine = engine;
        Icon = icon;
    }
}
