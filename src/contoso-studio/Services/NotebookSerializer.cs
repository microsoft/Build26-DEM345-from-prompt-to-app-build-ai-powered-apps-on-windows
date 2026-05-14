using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// JSON-on-disk shape for a notebook (the recipe — effects + per-step options,
/// no media). Versioned for forward compatibility.
/// v1: linear chain — { id, options } per step. Inputs implied by step order.
/// v2: typed-pin graph — { stepId, effectId, options, inputRefs } per step.
/// </summary>
public sealed class NotebookDocument
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "Untitled Notebook";
    public string? Description { get; set; }
    public List<NotebookStep> Steps { get; set; } = new();
}

public sealed class NotebookStep
{
    /// <summary>v1 alias for EffectId. Kept for backward compatibility on load.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>v2: per-instance node id (distinct from EffectId so duplicates work).</summary>
    public string? StepId { get; set; }
    /// <summary>v2: catalog effect id (e.g. "transcribe", "caption-burn"). Falls back to Id for v1.</summary>
    public string? EffectId { get; set; }
    public Dictionary<string, JsonElement> Options { get; set; } = new();
    /// <summary>v2: pinId → upstream { stepId, pinId, kind }. Empty for v1 (resolver implicit fallback used).</summary>
    public Dictionary<string, NotebookInputRef> InputRefs { get; set; } = new();
}

public sealed class NotebookInputRef
{
    public string StepId { get; set; } = string.Empty;
    public string PinId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// Save / load a pipeline as a `.studionb` JSON file. Only the recipe is
/// persisted (effect IDs + their per-step options) — media files and run
/// outputs are not.
/// </summary>
public static class NotebookSerializer
{
    public const string FileExtension = ".studionb";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static NotebookDocument FromSteps(IEnumerable<PipelineStep> steps, string name = "Untitled Notebook", string? description = null)
    {
        var doc = new NotebookDocument { Name = name, Description = description };
        foreach (var step in steps)
        {
            var ns = new NotebookStep
            {
                Id = step.Id,           // v1 alias
                StepId = step.StepId,   // v2 per-instance id
                EffectId = step.Id,     // v2 catalog effect id (currently same as Id)
                Options = SerializeStepOptions(step),
            };
            foreach (var kv in step.Inputs)
            {
                ns.InputRefs[kv.Key] = new NotebookInputRef
                {
                    StepId = kv.Value.StepId,
                    PinId = kv.Value.PinId,
                    Kind = kv.Value.Kind.ToString()
                };
            }
            doc.Steps.Add(ns);
        }
        return doc;
    }

    public static string SerializeToJson(NotebookDocument doc) =>
        JsonSerializer.Serialize(doc, JsonOpts);

    public static void Save(string path, NotebookDocument doc)
    {
        string json = SerializeToJson(doc);
        File.WriteAllText(path, json);
    }

    public static NotebookDocument Load(string path)
    {
        string json = File.ReadAllText(path);
        return Parse(json);
    }

    public static NotebookDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<NotebookDocument>(json, JsonOpts)
            ?? throw new InvalidDataException("Notebook JSON is empty or invalid.");
        if (doc.Version > 2)
            throw new InvalidDataException($"Notebook version {doc.Version} is newer than this app supports (2).");
        // v1 → v2 adapter: copy Id → EffectId so downstream apply uses the canonical field.
        foreach (var s in doc.Steps)
        {
            if (string.IsNullOrEmpty(s.EffectId)) s.EffectId = s.Id;
        }
        return doc;
    }

    /// <summary>
    /// Apply a parsed notebook to a fresh pipeline: clears existing steps,
    /// then adds each effect by ID (auto-resolving its dependencies via the
    /// VM) and restores each step's options from JSON.
    /// </summary>
    public static int ApplyTo(NotebookDocument doc, ViewModels.PipelineViewModel vm)
    {
        if (vm == null) throw new ArgumentNullException(nameof(vm));
        vm.ClearSteps();

        // Pass 1: insert each step, restore options + StepId. Build map old→new step.
        var added = new List<(NotebookStep Saved, PipelineStep Step)>();
        int loaded = 0;
        foreach (var ns in doc.Steps)
        {
            string effectId = !string.IsNullOrEmpty(ns.EffectId) ? ns.EffectId! : ns.Id;
            var effect = vm.AvailableEffects.FirstOrDefault(e => string.Equals(e.Id, effectId, StringComparison.OrdinalIgnoreCase));
            if (effect == null) continue; // unknown effect — skip (best effort)
            int beforeCount = vm.Steps.Count;
            vm.InsertStep(-1, effect);
            var addedStep = vm.Steps.FirstOrDefault(s => string.Equals(s.Id, effect.Id, StringComparison.OrdinalIgnoreCase)
                                                       && !added.Any(a => ReferenceEquals(a.Step, s)));
            if (addedStep != null)
            {
                ApplyOptions(addedStep, ns.Options);
                // v2: restore StepId so InputRefs that reference it from other steps remain valid.
                if (!string.IsNullOrEmpty(ns.StepId)) addedStep.StepId = ns.StepId!;
                added.Add((ns, addedStep));
                if (vm.Steps.Count > beforeCount) loaded++;
            }
        }

        // Pass 2: resolve InputRefs (v2 only — v1 leaves Inputs empty and uses implicit fallback).
        foreach (var (saved, step) in added)
        {
            if (saved.InputRefs.Count == 0) continue;
            foreach (var kv in saved.InputRefs)
            {
                if (!Enum.TryParse<ArtifactKind>(kv.Value.Kind, true, out var kind)) continue;
                step.Inputs[kv.Key] = new ArtifactRef
                {
                    StepId = kv.Value.StepId,
                    PinId = kv.Value.PinId,
                    Kind = kind
                };
            }
        }

        // Phase 8.8.1: backfill any pin still missing a ref so layout/wires are complete.
        // Pass-1 InsertStep ran backfill before downstream StepIds were restored, so re-run.
        // Existing explicit refs (Pass 2) are preserved by BackfillInputRefs (it skips filled pins).
        // Source-prefixed refs are dropped+re-synthesized so they target the current SourceFile.
        vm.BackfillAllInputRefs();
        return loaded;
    }

    private static Dictionary<string, JsonElement> SerializeStepOptions(PipelineStep step)
    {
        // Serialize only the fields that are user-tunable per effect. Round-trips
        // through System.Text.Json so the values become JsonElements.
        var raw = new Dictionary<string, object?>();

        // Transcribe
        raw[nameof(step.WhisperModel)] = step.WhisperModel.ToString();
        raw[nameof(step.TranscribeLanguage)] = step.TranscribeLanguage;
        raw[nameof(step.TranscribeTranslate)] = step.TranscribeTranslate;
        raw[nameof(step.HardwarePreference)] = step.HardwarePreference.ToString();

        // Detect Objects
        raw[nameof(step.ConfidenceThreshold)] = step.ConfidenceThreshold;
        raw[nameof(step.SampleFrameCount)] = step.SampleFrameCount;

        // Detect Silence
        raw[nameof(step.SilenceThresholdDb)] = step.SilenceThresholdDb;
        raw[nameof(step.MinSilenceMs)] = step.MinSilenceMs;
        raw[nameof(step.SilencePadMs)] = step.SilencePadMs;

        // Smart Cut
        raw[nameof(step.MinKeepMs)] = step.MinKeepMs;

        // Chapter Markers
        raw[nameof(step.ChapterTargetCount)] = step.ChapterTargetCount;

        // Highlight Picker
        raw[nameof(step.HighlightTargetCount)] = step.HighlightTargetCount;
        raw[nameof(step.HighlightClipSeconds)] = step.HighlightClipSeconds;

        // Round-trip → JsonElements so the resulting dictionary is uniformly typed.
        var json = JsonSerializer.Serialize(raw, JsonOpts);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
            ?? new Dictionary<string, JsonElement>();
    }

    private static void ApplyOptions(PipelineStep step, IReadOnlyDictionary<string, JsonElement> options)
    {
        if (TryGet(options, nameof(step.WhisperModel), out var v) && Enum.TryParse<WhisperModelSize>(v.GetString(), true, out var ws)) step.WhisperModel = ws;
        if (TryGet(options, nameof(step.TranscribeLanguage), out v) && v.ValueKind == JsonValueKind.String) step.TranscribeLanguage = v.GetString() ?? step.TranscribeLanguage;
        if (TryGet(options, nameof(step.TranscribeTranslate), out v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) step.TranscribeTranslate = v.GetBoolean();
        if (TryGet(options, nameof(step.HardwarePreference), out v) && Enum.TryParse<HardwarePreference>(v.GetString(), true, out var hp)) step.HardwarePreference = hp;

        if (TryGet(options, nameof(step.ConfidenceThreshold), out v) && v.TryGetDouble(out var d1)) step.ConfidenceThreshold = d1;
        if (TryGet(options, nameof(step.SampleFrameCount), out v) && v.TryGetInt32(out var i1)) step.SampleFrameCount = i1;

        if (TryGet(options, nameof(step.SilenceThresholdDb), out v) && v.TryGetDouble(out var d2)) step.SilenceThresholdDb = d2;
        if (TryGet(options, nameof(step.MinSilenceMs), out v) && v.TryGetInt32(out var i2)) step.MinSilenceMs = i2;
        if (TryGet(options, nameof(step.SilencePadMs), out v) && v.TryGetInt32(out var i3)) step.SilencePadMs = i3;

        if (TryGet(options, nameof(step.MinKeepMs), out v) && v.TryGetInt32(out var i4)) step.MinKeepMs = i4;

        if (TryGet(options, nameof(step.ChapterTargetCount), out v) && v.TryGetInt32(out var i5)) step.ChapterTargetCount = i5;

        if (TryGet(options, nameof(step.HighlightTargetCount), out v) && v.TryGetInt32(out var i6)) step.HighlightTargetCount = i6;
        if (TryGet(options, nameof(step.HighlightClipSeconds), out v) && v.TryGetInt32(out var i7)) step.HighlightClipSeconds = i7;
    }

    private static bool TryGet(IReadOnlyDictionary<string, JsonElement> opts, string key, out JsonElement value)
    {
        if (opts.TryGetValue(key, out value)) return true;
        // Case-insensitive fallback (handles camelCase / PascalCase variants).
        foreach (var kv in opts)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
