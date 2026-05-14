using System.Collections.Generic;
using System.IO;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Resolves typed artifact inputs for a step. Phase 8.3.
///
/// Lookup order for each requested pin:
/// 1. Explicit wiring: <c>step.Inputs[pinId]</c> -> follow ArtifactRef -> upstream step's <c>Outputs[refPinId]</c>.
/// 2. Implicit fallback: walk <c>allSteps</c> backward from this step and return the first
///    upstream step's matching <c>Outputs[pinId]</c> (or any Outputs entry with the requested Kind).
///
/// This preserves legacy linear behavior (e.g. Burn Captions auto-finding the most recent
/// Whisper transcript) while letting users override the source via the UI when multiple
/// candidates exist (Phase 8.4 vertical slice).
/// </summary>
public static class ArtifactResolver
{
    /// <summary>
    /// Try to resolve a single typed artifact input.
    /// </summary>
    /// <typeparam name="TArtifact">Concrete artifact subtype (e.g. <see cref="TranscriptArtifact"/>).</typeparam>
    /// <param name="step">The step that wants the input.</param>
    /// <param name="pinId">Pin id from the consuming effect's <see cref="Effect.InputPins"/>.</param>
    /// <param name="allSteps">All steps in the pipeline, in linear order.</param>
    /// <param name="sourceStep">When found, the upstream step that produced the artifact (for logging).</param>
    public static TArtifact? GetInput<TArtifact>(
        PipelineStep step,
        string pinId,
        IList<PipelineStep> allSteps,
        out PipelineStep? sourceStep) where TArtifact : Artifact
    {
        sourceStep = null;

        // 1. Explicit wiring via ArtifactRef.
        if (step.Inputs.TryGetValue(pinId, out var explicitRef))
        {
            var src = FindStepByStepId(allSteps, explicitRef.StepId);
            if (src != null && src.Outputs.TryGetValue(explicitRef.PinId, out var explicitArtifact)
                && explicitArtifact is TArtifact explicitTyped)
            {
                sourceStep = src;
                return explicitTyped;
            }
            // Explicit ref pointed at something that no longer exists (or wrong type).
            // Fall through to implicit lookup as a safety net.
        }

        // 2. Implicit fallback: scan backward through allSteps for first matching Outputs.
        int idx = allSteps.IndexOf(step);
        if (idx < 0) idx = allSteps.Count; // step not in list (defensive); search whole list backward
        for (int i = idx - 1; i >= 0; i--)
        {
            var candidate = allSteps[i];
            // Prefer exact pin id match
            if (candidate.Outputs.TryGetValue(pinId, out var byPin) && byPin is TArtifact byPinTyped)
            {
                sourceStep = candidate;
                return byPinTyped;
            }
            // Otherwise any output of the requested type
            foreach (var kv in candidate.Outputs)
            {
                if (kv.Value is TArtifact typed)
                {
                    sourceStep = candidate;
                    return typed;
                }
            }
        }

        return null;
    }

    public static PipelineStep? FindStepByStepId(IList<PipelineStep> allSteps, string stepId)
    {
        foreach (var s in allSteps)
            if (s.StepId == stepId) return s;
        return null;
    }

    /// <summary>
    /// Per-step scoped work directory. Files written here won't collide across fan-out branches
    /// or batch runs, unlike the historical fixed names like <c>audio.wav</c>, <c>silence.json</c>.
    /// </summary>
    public static string EnsureStepWorkDir(string runWorkDir, PipelineStep step)
    {
        // Use first 12 chars of StepId for shorter paths; full uniqueness still preserved for tracing
        // because we also include the effect id. Format: <runDir>/<effectId>_<stepId12>/
        string slug = $"{Sanitize(step.Id)}_{step.StepId.Substring(0, System.Math.Min(12, step.StepId.Length))}";
        string dir = Path.Combine(runWorkDir, slug);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
        return new string(chars);
    }
}
