#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    // deadline, indistinguishable from a dead game.
    internal static class MainThreadWatchdog
    {
        internal const double StallSeconds = 2.0;
        private const string Prefix = "[RimGovernor] main-thread hop ";

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
            if (hop == null || !hop.Reported) return;
            var now = Stopwatch.GetTimestamp();
            Log.Warning(Prefix + "recovered: " + hop.Describe() + " queuedMs=" + Millis((hop.Started == 0 ? now : hop.Started) - hop.Queued)
                + " executeMs=" + Millis(hop.Started == 0 ? 0 : now - hop.Started));
        }

        private static void Scan()
        {
            var now = Stopwatch.GetTimestamp();
            var stalled = new List<string>();
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
                        stalled.Add("queued " + waited.ToString("F1") + "s without the game thread picking it up: " + hop.Describe());
                    }
                    else
                    {
                        var running = Seconds(now - hop.Started);
                        if (running < StallSeconds) continue;
                        hop.Reported = true;
                        stalled.Add("running " + running.ToString("F1") + "s on the game thread: " + hop.Describe());
                    }
                }
            }
            foreach (var line in stalled)
            {
                try { Log.Warning(Prefix + line); } catch (Exception) { }
            }
        }

        private static double Seconds(long ticks) => ticks <= 0 ? 0.0 : ticks / (double)Stopwatch.Frequency;
        private static double Millis(long ticks) => ticks <= 0 ? 0.0 : Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);
    }
}
