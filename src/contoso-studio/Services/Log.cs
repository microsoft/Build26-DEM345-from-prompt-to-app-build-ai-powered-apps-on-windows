using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VideoStudio.Services;

/// <summary>
/// Simple app-level logger that writes to Debug output (visible in --debug-output)
/// and optionally to a file. All messages prefixed with [ContosoStudio] for filtering.
/// </summary>
public static class Log
{
    private const string Tag = "ContosoStudio";

    public static void Info(string message, [CallerMemberName] string? caller = null)
    {
        var line = $"[{Tag}] {caller}: {message}";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    public static void Warn(string message, [CallerMemberName] string? caller = null)
    {
        var line = $"[{Tag}] ⚠ {caller}: {message}";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null)
    {
        var line = $"[{Tag}] ❌ {caller}: {message}";
        if (ex != null)
            line += $"\n  → {ex.GetType().Name}: {ex.Message}\n  {ex.StackTrace?.Split('\n')[0]}";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }

    public static void Step(string stepName, string message)
    {
        var line = $"[{Tag}] [{stepName}] {message}";
        Debug.WriteLine(line);
        Trace.WriteLine(line);
    }
}
