using System.Runtime.InteropServices;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The Win32 surface of the game host.</summary>
internal static partial class GameHostNative
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint CreateNewProcessGroup = 0x00000200;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateBreakawayFromJob = 0x01000000;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const int StartfUseStdHandles = 0x00000100;
    internal const nint ProcThreadAttributeHandleList = 0x00020002;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    /// <summary>STARTUPINFOEXW: the startup info plus the attribute list naming the only handles the child inherits.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int TerminateProcess(IntPtr process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateProcess(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        int inheritHandles,
        uint creationFlags,
        string? environment,
        string? currentDirectory,
        in StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(IntPtr attributeList);
}
