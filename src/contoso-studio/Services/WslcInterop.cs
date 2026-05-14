using System;
using System.Runtime.InteropServices;

namespace VideoStudio.Services;

/// <summary>
/// P/Invoke declarations for wslcsdk.dll — the WSL Container SDK.
/// </summary>
internal static partial class WslcInterop
{
    private const string DllName = "wslcsdk.dll";

    // Struct sizes from the header
    public const int SessionSettingsSize = 80;
    public const int ContainerSettingsSize = 96;
    public const int ProcessSettingsSize = 72;
    public const int ContainerIdBufferSize = 65;
    public const int ImageNameLength = 256;

    // --- Enums ---

    public enum ContainerNetworkingMode { None = 0, Bridged = 1 }

    [Flags]
    public enum SessionFeatureFlags : uint
    {
        None = 0x00000000,
        EnableGpu = 0x00000004
    }

    [Flags]
    public enum ContainerFlags : uint
    {
        None = 0x00000000,
        AutoRemove = 0x00000001,
        EnableGpu = 0x00000002,
        Privileged = 0x00000004
    }

    [Flags]
    public enum ContainerStartFlags : uint
    {
        None = 0x00000000,
        Attach = 0x00000001
    }

    public enum ContainerState { Invalid = 0, Created = 1, Running = 2, Exited = 3, Deleted = 4 }

    public enum Signal { None = 0, SigHup = 1, SigInt = 2, SigQuit = 3, SigKill = 9, SigTerm = 15 }

    [Flags]
    public enum DeleteContainerFlags : uint { None = 0, Force = 0x01 }

    public enum ProcessIOHandle { Stdin = 0, Stdout = 1, Stderr = 2 }

    public enum ProcessState { Unknown = 0, Running = 1, Exited = 2, Signalled = 3 }

    public enum ImageProgressStatus
    {
        Unknown = 0, Pulling = 1, Waiting = 2, Downloading = 3,
        Verifying = 4, Extracting = 5, Complete = 6
    }

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential, Size = SessionSettingsSize)]
    public struct SessionSettings { }

    [StructLayout(LayoutKind.Sequential, Size = ContainerSettingsSize)]
    public struct ContainerSettings { }

    [StructLayout(LayoutKind.Sequential, Size = ProcessSettingsSize)]
    public struct ProcessSettings { }

    [StructLayout(LayoutKind.Sequential)]
    public struct ContainerVolume
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string WindowsPath;
        [MarshalAs(UnmanagedType.LPStr)] public string ContainerPath;
        public int ReadOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageProgressDetail
    {
        public ulong Current;
        public ulong Total;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageProgressMessage
    {
        public IntPtr Id; // PCSTR
        public ImageProgressStatus Status;
        public ImageProgressDetail Detail;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PullImageOptions
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Uri;
        public IntPtr ProgressCallback;
        public IntPtr ProgressCallbackContext;
        public IntPtr AuthInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WslcVersion
    {
        public uint Major;
        public uint Minor;
        public uint Revision;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessCallbacks
    {
        public IntPtr OnStdOut;
        public IntPtr OnStdErr;
        public IntPtr OnExit;
    }

    // --- Delegates ---

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int ImageProgressCallback(ref ImageProgressMessage progress, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void StdIOCallback(ProcessIOHandle ioHandle, IntPtr data, uint dataSize, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void ProcessExitCallback(int exitCode, IntPtr context);

    // --- Session APIs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcInitSessionSettings(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string storagePath,
        out SessionSettings sessionSettings);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetSessionSettingsCpuCount(ref SessionSettings sessionSettings, uint cpuCount);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetSessionSettingsMemory(ref SessionSettings sessionSettings, uint memoryMb);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetSessionSettingsTimeout(ref SessionSettings sessionSettings, uint timeoutMs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetSessionSettingsFeatureFlags(ref SessionSettings sessionSettings, SessionFeatureFlags flags);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcCreateSession(ref SessionSettings sessionSettings, out IntPtr session, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcTerminateSession(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcReleaseSession(IntPtr session);

    // --- Container APIs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcInitContainerSettings(
        [MarshalAs(UnmanagedType.LPStr)] string imageName,
        out ContainerSettings containerSettings);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetContainerSettingsName(ref ContainerSettings containerSettings,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetContainerSettingsInitProcess(ref ContainerSettings containerSettings, ref ProcessSettings initProcess);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetContainerSettingsNetworkingMode(ref ContainerSettings containerSettings, ContainerNetworkingMode networkingMode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetContainerSettingsFlags(ref ContainerSettings containerSettings, ContainerFlags flags);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetContainerSettingsVolumes(ref ContainerSettings containerSettings,
        [MarshalAs(UnmanagedType.LPArray)] ContainerVolume[]? volumes, uint volumeCount);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcCreateContainer(IntPtr session, ref ContainerSettings containerSettings, out IntPtr container, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcStartContainer(IntPtr container, ContainerStartFlags flags, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcStopContainer(IntPtr container, Signal signal, uint timeoutSeconds, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcDeleteContainer(IntPtr container, DeleteContainerFlags flags, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcReleaseContainer(IntPtr container);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetContainerState(IntPtr container, out ContainerState state);

    // --- Process APIs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcInitProcessSettings(out ProcessSettings processSettings);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetProcessSettingsCmdLine(ref ProcessSettings processSettings,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv, nint argc);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcSetProcessSettingsCallbacks(ref ProcessSettings processSettings, ref ProcessCallbacks callbacks, IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcCreateContainerProcess(IntPtr container, ref ProcessSettings processSettings, out IntPtr process, out IntPtr errorMessage);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetProcessIOHandle(IntPtr process, ProcessIOHandle ioHandle, out IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetProcessExitEvent(IntPtr process, out IntPtr exitEvent);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetProcessExitCode(IntPtr process, out int exitCode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetProcessState(IntPtr process, out ProcessState state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcReleaseProcess(IntPtr process);

    // --- Image APIs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcPullSessionImage(IntPtr session, ref PullImageOptions options, out IntPtr errorMessage);

    // --- Install APIs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcCanRun(out int canRun, out uint missingComponents);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int WslcGetVersion(out WslcVersion version);

    // --- Helpers ---

    public static string? GetErrorMessage(IntPtr errorPtr)
    {
        if (errorPtr == IntPtr.Zero) return null;
        string? msg = Marshal.PtrToStringUni(errorPtr);
        Marshal.FreeCoTaskMem(errorPtr);
        return msg;
    }

    public static void ThrowIfFailed(int hr, IntPtr errorPtr, string operation)
    {
        if (hr >= 0) return; // S_OK or success
        string? errorMsg = GetErrorMessage(errorPtr);
        throw new InvalidOperationException(
            $"WSLC {operation} failed (0x{hr:X8}): {errorMsg ?? "unknown error"}");
    }
}
