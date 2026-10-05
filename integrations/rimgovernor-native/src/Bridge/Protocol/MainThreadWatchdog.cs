#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Names a main-thread hop that has stalled (#600). Every
    // ProtoBoundary.OnMainThread hop registers here when queued, marks itself
    // started when the game thread picks it up, and unregisters when its body
    // returns. A background timer scans the live hops once a second and logs,
    // once per hop, the first time one has waited in the queue longer than
    // StallSeconds (the game thread is not pumping: a long event, a modal OS
    // dialog, the window not updating) or has been running longer than
    // StallSeconds (the body itself is stuck), naming the operation and the
    // controller's trace ("<trace_id>/<span_id>") so the line joins the
    // service's flight log. Without it a hung call shows only as a Go-side
    // deadline, indistinguishable from a dead game. The lines go through
    // ModLog (the rimgovernor.log channel, #2058), not Unity's log: they reach
    // the flight recorder as mod_log rows carrying the same trace.
    internal static class MainThreadWatchdog
    {
        internal const double StallSeconds = 2.0;
        internal const double SlowMillis = 100.0;
        private const string Component = "watchdog";

        internal sealed class Hop
        {
            internal readonly string Operation;
            internal readonly string? Trace;
            internal readonly long Queued;
            internal long Started;
            internal bool Reported;
            internal Hop(string operation, string? trace, long queued) { Operation = operation; Trace = trace; Queued = queued; }
            internal string Describe() => Operation + (Trace == null ? "" : " trace=" + Trace);
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Hop> Live = new Dictionary<long, Hop>();
        private static long next;
        private static Timer? timer;

        internal static long Enqueue(string operation, string? trace)
        {
            var hop = new Hop(operation, trace, Stopwatch.GetTimestamp());
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
                // A hop over the live-play frame budget (#984) but under the
                // stall line still names itself, so a choppy session shows
                // which op held the game thread.
                var executeMs = Millis(hop.Started == 0 ? 0 : now - hop.Started);
                if (executeMs >= SlowMillis) ModLog.Warn(Component, "hop slow: " + hop.Describe() + " executeMs=" + executeMs, hop.Trace, "watchdog.slow:" + hop.Operation);
                return;
            }
            ModLog.Warn(Component, "hop recovered: " + hop.Describe() + " queuedMs=" + Millis((hop.Started == 0 ? now : hop.Started) - hop.Queued)
                + " executeMs=" + Millis(hop.Started == 0 ? 0 : now - hop.Started), hop.Trace, "watchdog.recovered:" + hop.Operation);
        }

        private static void Scan()
        {
            var now = Stopwatch.GetTimestamp();
            var stalled = new List<Hop>();
            var lines = new List<string>();
            lock (Gate)
            {
                foreach (var hop in Live.Values)
                {
                    if (hop.Reported) continue;
                    if (hop.Started == 0)
                    {
                        var waited = Seconds(now - hop.Queued);
                        if (waited < StallSeconds) continue;
                        hop.Reported = true;
                        stalled.Add(hop);
                        lines.Add("queued " + waited.ToString("F1") + "s without the game thread picking it up: " + hop.Describe());
                    }
                    else
                    {
                        var running = Seconds(now - hop.Started);
                        if (running < StallSeconds) continue;
                        hop.Reported = true;
                        stalled.Add(hop);
                        lines.Add("running " + running.ToString("F1") + "s on the game thread: " + hop.Describe());
                    }
                }
            }
            for (var i = 0; i < stalled.Count; i++)
                ModLog.Warn(Component, "hop " + lines[i], stalled[i].Trace, "watchdog.stall:" + stalled[i].Operation);
        }

        private static double Seconds(long ticks) => ticks <= 0 ? 0.0 : ticks / (double)Stopwatch.Frequency;
        private static double Millis(long ticks) => ticks <= 0 ? 0.0 : Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);
    }
}
