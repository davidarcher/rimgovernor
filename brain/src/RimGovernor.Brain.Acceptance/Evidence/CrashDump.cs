using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The game runtime's own crash dump (#3190). RimWorld 1.7 runs on a self-contained CoreCLR that ships
/// <c>createdump.exe</c>; with these variables in a launch's environment the runtime writes a minidump of a crashing
/// process into the case's output directory. It is how an intermittent startup crash (an <c>Internal CLR error</c> in
/// MonoMod's JIT compile hook, the process exiting with 0xC0000005 before its channel comes up) leaves more than a log
/// line. The dump holds process memory, so it stays beside the case evidence and is never exported publicly.
/// </summary>
public static class CrashDump
{
    public const string EnableVariable = "DOTNET_DbgEnableMiniDump";

    public const string TypeVariable = "DOTNET_DbgMiniDumpType";

    /// <summary>The dump path; differs per case like the state database, so it never forces a kept process to relaunch.</summary>
    public const string NameVariable = "DOTNET_DbgMiniDumpName";

    /// <summary>
    /// MiniDump (1): every thread's stack, registers and the module list, which is what a native fault in the runtime or
    /// a JIT hook needs, at a size a runner disk always holds (a heap dump of a loaded game runs to gigabytes).
    /// </summary>
    public const string MiniType = "1";

    /// <summary>The dump file pattern in a case output directory; createdump replaces <c>%p</c> with the crashing pid.</summary>
    public const string FilePattern = "coreclr-*.dmp";

    /// <summary>The exit code of a process killed by an access violation (0xC0000005), the startup crash's exit.</summary>
    public const int AccessViolationExit = unchecked((int)0xC0000005);

    /// <summary>The environment entries that make a launched game dump into <paramref name="output"/> when it crashes.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LaunchEnv(string output) =>
    [
        new(EnableVariable, "1"),
        new(TypeVariable, MiniType),
        new(NameVariable, Path.Combine(output, "coreclr-%p.dmp")),
    ];

    /// <summary>
    /// Whether <paramref name="error"/> is the startup crash: the game exited with an access violation before its channel
    /// came up (<see cref="GameSessions.ExitedBeforeChannel"/>).
    /// </summary>
    internal static bool IsStartupCrash(string error) =>
        error.Contains(GameSessions.ExitedBeforeChannel, StringComparison.Ordinal)
        && error.Contains(string.Create(CultureInfo.InvariantCulture, $"(exit code {AccessViolationExit})"), StringComparison.Ordinal);
}
