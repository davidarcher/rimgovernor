#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace HomeBridge.BridgeTools
{
    // The game thread's breadcrumb: the phase it last entered (a hop, the
    // frame prefix, the ticks, the frame postfix, a snapshot capture and the
    // section it last finished) and when. The watchdog prints it in every
    // stall line, so a hang that never recovers still says where the thread
    // was lost (#3576: a stall with no hop running and no stack). Marks are
    // two volatile writes; a torn read misdates a mark by one mark at most.
    // Game-free, so the brain's unit tests compile it in.
    internal static class GameThreadTrace
    {
        private static volatile string? phase;
        private static long at;

        internal static void Mark(string where)
        {
            Volatile.Write(ref at, Stopwatch.GetTimestamp());
            phase = where;
        }

        internal static string? Phase => phase;

        internal static double SilentSeconds(long now) => Math.Max(0.0, (now - Volatile.Read(ref at)) / (double)Stopwatch.Frequency);

        internal static string Describe(long now) => Describe(phase, now - Volatile.Read(ref at), Stopwatch.Frequency);

        internal static string Describe(string? phase, long sinceTicks, long frequency)
        {
            if (phase == null) return "game thread phase unknown";
            var seconds = Math.Max(0.0, sinceTicks / (double)frequency);
            return "game thread last seen " + seconds.ToString("F1", CultureInfo.InvariantCulture) + "s ago in: " + phase;
        }
    }

    // Decides the one stall dump a process writes: a hop has waited in the
    // queue DumpAfterSeconds and the game thread has marked nothing for as
    // long (a slow hop or a long frame marks; a lost thread does not).
    internal sealed class StallDumpPolicy
    {
        internal const double DumpAfterSeconds = 20.0;
        private int fired;

        internal bool Fired => Volatile.Read(ref fired) != 0;

        internal bool Due(double longestQueuedSeconds, double silentSeconds)
        {
            if (longestQueuedSeconds < DumpAfterSeconds || silentSeconds < DumpAfterSeconds) return false;
            return Interlocked.CompareExchange(ref fired, 1, 0) == 0;
        }
    }

    // The createdump command the watchdog runs against this process: the
    // tool ships beside the game executable (the .NET host's createdump.exe),
    // the dump is a minidump with heap (enough for managed stacks) named
    // stall-<pid>-<utc>.dmp. RIMGOVERNOR_STALL_DUMP=0 (or off/false/no) turns
    // it off; a test-acceleration launch never dumps.
    internal static class StallDump
    {
        internal const string EnableVariable = "RIMGOVERNOR_STALL_DUMP";
        internal const string ToolFileName = "createdump.exe";
        // The run folder (HttpChannelBootstrap.DirectoryVariable) and the test-acceleration launch flag
        // (Supervisor.TestAccelerationLaunch), restated so this file stays game-free.
        internal const string DirectoryVariable = "RIMGOVERNOR_OTLP_DIR";
        internal const string TestAccelerationArgument = "-rimgovernor-test-acceleration";

        internal static bool TestAccelerationLaunch(string[] commandLine) => Array.IndexOf(commandLine, TestAccelerationArgument) >= 0;

        internal static bool Enabled(string? variable, bool testAccelerationLaunch)
        {
            if (testAccelerationLaunch) return false;
            if (variable == null) return true;
            switch (variable.Trim().ToLowerInvariant())
            {
                case "0": case "off": case "false": case "no": return false;
                default: return true;
            }
        }

        internal static string? ToolPath(string? processPath)
        {
            if (string.IsNullOrEmpty(processPath)) return null;
            var directory = Path.GetDirectoryName(processPath);
            return directory == null ? null : Path.Combine(directory, ToolFileName);
        }

        internal static string FileName(int pid, DateTime utc)
            => "stall-" + pid.ToString(CultureInfo.InvariantCulture) + "-" + utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".dmp";

        internal static string Arguments(string path, int pid)
            => "--withheap --name \"" + path + "\" " + pid.ToString(CultureInfo.InvariantCulture);
    }
}
