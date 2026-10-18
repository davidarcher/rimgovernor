#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RimGovernor.Host.Sdk;

namespace HomeBridge.BridgeTools
{
    // Names a main-thread hop that has stalled. Every
    // ProtoBoundary.OnGameThread hop registers here when queued, marks itself
    // started when the game thread picks it up, and unregisters when its body
    // returns. A background timer scans the live hops once a second and logs,
    // once per hop, the first time one has waited in the queue longer than
    // StallSeconds (the game thread is not pumping: a long event, a modal OS
    // dialog, the window not updating) or has been running longer than
    // StallSeconds (the body itself is stuck), naming the operation and the
    // game thread's breadcrumb (GameThreadTrace: the phase it last entered
    // and how long ago). Without it a hung call shows only as the caller's
    // deadline, indistinguishable from a dead game. The lines go through
    // ModLog.
    //
    // Once per process, when a hop has waited StallDumpPolicy.DumpAfterSeconds
    // and the game thread has marked nothing for as long, the timer writes a
    // minidump of this process with the createdump beside the game executable
    // (#3576: a hang the log could not place, closed by Windows with no
    // stack). The dump lands beside the run's OTLP rings.
    internal static class MainThreadWatchdog
    {
        internal const double StallSeconds = 2.0;
        internal const double SlowMillis = 100.0;
        private const string Component = "watchdog";
        private static readonly TimeSpan DumpBudget = TimeSpan.FromMinutes(5);

        internal sealed class Hop
        {
            internal readonly string Operation;
            internal readonly long Queued;
            internal long Started;
            internal bool Reported;
            internal Hop(string operation, long queued) { Operation = operation; Queued = queued; }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Hop> Live = new Dictionary<long, Hop>();
        private static readonly StallDumpPolicy Dump = new StallDumpPolicy();
        private static long next;
        private static Timer? timer;

        internal static long Enqueue(string operation)
        {
            var hop = new Hop(operation, Stopwatch.GetTimestamp());
            lock (Gate)
            {
                var id = ++next;
                Live[id] = hop;
                if (timer == null) timer = new Timer(_ => Scan(), null, 1000, 1000);
                return id;
            }
        }

        internal static void Start(long id)
        {
            lock (Gate) { if (Live.TryGetValue(id, out var hop)) hop.Started = Stopwatch.GetTimestamp(); }
        }

        internal static void Finish(long id)
        {
            Hop? hop;
            lock (Gate) { if (Live.TryGetValue(id, out hop)) Live.Remove(id); }
            if (hop == null) return;
            var now = Stopwatch.GetTimestamp();
            if (!hop.Reported)
            {
                // A hop over the live-play frame budget but under the
                // stall line still names itself, so a choppy session shows
                // which op held the game thread.
                var executeMs = Millis(hop.Started == 0 ? 0 : now - hop.Started);
                if (executeMs >= SlowMillis) ModLog.Warn(Component, "hop slow: " + hop.Operation + " executeMs=" + executeMs, "watchdog.slow:" + hop.Operation);
                return;
            }
            ModLog.Warn(Component, "hop recovered: " + hop.Operation + " queuedMs=" + Millis((hop.Started == 0 ? now : hop.Started) - hop.Queued)
                + " executeMs=" + Millis(hop.Started == 0 ? 0 : now - hop.Started), "watchdog.recovered:" + hop.Operation);
        }

        private static void Scan()
        {
            var now = Stopwatch.GetTimestamp();
            var stalled = new List<Hop>();
            var lines = new List<string>();
            var longestQueued = 0.0;
            lock (Gate)
            {
                foreach (var hop in Live.Values)
                {
                    if (hop.Started == 0) longestQueued = Math.Max(longestQueued, Seconds(now - hop.Queued));
                    if (hop.Reported) continue;
                    if (hop.Started == 0)
                    {
                        var waited = Seconds(now - hop.Queued);
                        if (waited < StallSeconds) continue;
                        hop.Reported = true;
                        stalled.Add(hop);
                        lines.Add("queued " + waited.ToString("F1") + "s without the game thread picking it up: " + hop.Operation);
                    }
                    else
                    {
                        var running = Seconds(now - hop.Started);
                        if (running < StallSeconds) continue;
                        hop.Reported = true;
                        stalled.Add(hop);
                        lines.Add("running " + running.ToString("F1") + "s on the game thread: " + hop.Operation);
                    }
                }
            }
            if (stalled.Count > 0)
            {
                var seen = GameThreadTrace.Describe(now);
                for (var i = 0; i < stalled.Count; i++)
                    ModLog.Warn(Component, "hop " + lines[i] + "; " + seen, "watchdog.stall:" + stalled[i].Operation);
            }
            // Before the first mark the thread's silence is the queue wait itself.
            var silent = GameThreadTrace.Phase == null ? longestQueued : GameThreadTrace.SilentSeconds(now);
            if (Dump.Due(longestQueued, silent)) ThreadPool.QueueUserWorkItem(_ => WriteDump(longestQueued));
        }

        // Writes the minidump off the timer thread (the write takes seconds)
        // and logs where it went or why it did not.
        private static void WriteDump(double stalledSeconds)
        {
            var stalled = "game thread stalled " + stalledSeconds.ToString("F1") + "s";
            try
            {
                if (!StallDump.Enabled(Environment.GetEnvironmentVariable(StallDump.EnableVariable), StallDump.TestAccelerationLaunch(Environment.GetCommandLineArgs())))
                { ModLog.Warn(Component, stalled + "; stall dump off"); return; }
                var tool = StallDump.ToolPath(Environment.ProcessPath);
                if (tool == null || !File.Exists(tool))
                { ModLog.Warn(Component, stalled + "; no " + StallDump.ToolFileName + " beside " + (Environment.ProcessPath ?? "?")); return; }
                var directory = Environment.GetEnvironmentVariable(StallDump.DirectoryVariable);
                if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetTempPath();
                var pid = Environment.ProcessId;
                var path = Path.Combine(directory, StallDump.FileName(pid, DateTime.UtcNow));
                ModLog.Warn(Component, stalled + "; writing " + path);
                var info = new ProcessStartInfo(tool, StallDump.Arguments(path, pid))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(info) ?? throw new InvalidOperationException(StallDump.ToolFileName + " did not start");
                var errors = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit((int)DumpBudget.TotalMilliseconds))
                {
                    try { process.Kill(); } catch (Exception) { }
                    ModLog.Warn(Component, "stall dump timed out after " + DumpBudget.TotalMinutes + " min: " + path);
                    return;
                }
                var said = (output + errors.Result).Trim();
                ModLog.Warn(Component, "stall dump " + (process.ExitCode == 0 ? "written" : "failed (exit " + process.ExitCode + ")") + ": " + path
                    + (said.Length == 0 ? "" : " " + said));
            }
            catch (Exception e)
            {
                ModLog.Warn(Component, "stall dump failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static double Seconds(long ticks) => ticks <= 0 ? 0.0 : ticks / (double)Stopwatch.Frequency;
        private static double Millis(long ticks) => ticks <= 0 ? 0.0 : Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);
    }
}
