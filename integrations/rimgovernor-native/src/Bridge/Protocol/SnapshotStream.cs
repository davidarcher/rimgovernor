using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using UnityEngine;
using Verse;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Native
{
    /// <summary>
    /// The snapshot stream: whole BundleSnapshot frames captured on the game
    /// thread and handed to the in-process brain (the facade's frame channel,
    /// latest wins). The game thread captures a frame right after a frame's
    /// ticks (Supervisor.OnFrame) every PeriodTicks, on a pause edge and after
    /// every applied write (a deferred write waits for observations_flush_snapshot
    /// or DeferredSeconds); a worker publishes it, so the game thread never
    /// waits on a reader. Nothing is captured until a subscriber attaches: the
    /// brain through the facade, or the acceptance harness through the
    /// observations_read_snapshot op, which pulls the newest frame.
    /// </summary>
    internal static class SnapshotStream
    {
        internal const int PeriodTicks = 60;
        internal const long PausedPeriodSeconds = 1;

        private static readonly object Gate = new object();

        /// The in-process subscriber's slot (the native facade's frame channel): latest wins, whole frames.
        internal static readonly RimGovernor.Brain.Bridge.FrameHandoff Handoff = new RimGovernor.Brain.Bridge.FrameHandoff();
        private static RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest subscription = new RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest { RoutePathBudget = new RimGovernor.Brain.GameFacts.Observations.FrameBudget.Unlimited(), LinesOfFireBudget = new RimGovernor.Brain.GameFacts.Observations.FrameBudget.Unlimited() };

        // The pull subscriber (the acceptance harness's observations_read_snapshot tool): the newest published
        // frame and how many were published since the first read. Guarded by Gate.
        private static bool pull;
        private static ulong pullHead;
        private static RimGovernor.Brain.GameFacts.Observations.BundleSnapshot? pullFrame;
        private static long pullFrameWrites;

        /// The shape the stream captures now (the default request until a
        /// controller subscribes); test/profile_capture profiles it.
        internal static RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest Subscription { get { lock (Gate) return subscription; } }
        // Game-thread state: what the last capture saw.
        private static long writes, capturedWrites = -1;
        // due: the write count the next frame must reflect. A deferred
        // write leaves it behind writes until a flush, a later
        // undeferred write, or DeferredSeconds after the oldest uncaptured
        // deferred write (deferredAt, game thread; 0: none).
        private static long due;
        private static long deferredAt;
        internal const long DeferredSeconds = 1;
        private static int capturedTick = int.MinValue;
        private static bool capturedPaused;
        private static int pending; // captures handed to the publisher, not yet published

        /// On the game thread: an applied write the next frame must reflect,
        /// or, deferred, one a flush or the safety net makes it capture.
        internal static void NoteWrite(bool deferred = false)
        {
            var value = Interlocked.Increment(ref writes);
            if (!deferred) { RaiseDue(value); return; }
            if (deferredAt == 0) deferredAt = Stopwatch.GetTimestamp();
        }

        /// Any thread: the next frame captures every write applied so far.
        /// Reports whether a write was uncaptured.
        internal static bool Flush()
        {
            var w = Interlocked.Read(ref writes);
            RaiseDue(w);
            return w != Interlocked.Read(ref capturedWrites);
        }

        private static void RaiseDue(long value)
        {
            long seen;
            while ((seen = Interlocked.Read(ref due)) < value)
                if (Interlocked.CompareExchange(ref due, value, seen) == seen) return;
        }

        /// Opens (or resubscribes) the stream for the in-process subscriber: frames go to Handoff.
        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply Open(RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request)
        {
            lock (Gate)
            {
                // Frames are captured from the supervised-play frame hook; a
                // stream opened before any clock start installs it itself.
                try
                {
                    Supervisor.EnsurePatched();
                    Handoff.Attach();
                }
                catch (Exception e)
                {
                    return new RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply { Unavailable = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "Snapshot stream could not be opened: " + e.Message } };
                }
                subscription = request;
                capturedWrites = -1; // the new subscription publishes at once
                return new RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply();
            }
        }

        /// The newest published frame for a pull subscriber. The first read subscribes (the stream keeps the
        /// shape the brain set, else the default) and makes the next frame capture.
        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotReadReply Read(RimGovernor.Brain.GameOrders.Observations.SnapshotReadRequest request)
        {
            lock (Gate)
            {
                if (!pull)
                {
                    try { Supervisor.EnsurePatched(); }
                    catch (Exception e)
                    {
                        return new RimGovernor.Brain.GameFacts.Observations.SnapshotReadReply { Outcome = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "Snapshot stream could not be opened: " + e.Message } };
                    }
                    pull = true;
                    capturedWrites = -1;
                }
                var newer = pullFrame != null && pullHead > request.AfterNumber;
                return new RimGovernor.Brain.GameFacts.Observations.SnapshotReadReply
                {
                    Outcome = new RimGovernor.Brain.GameFacts.Observations.SnapshotRead
                    {
                        Head = pullHead,
                        Writes = Interlocked.Read(ref writes),
                        FrameWrites = pullFrameWrites,
                        Frame = newer ? pullFrame : null,
                    },
                };
            }
        }

        /// The TickManagerUpdate postfix, on the game thread, once per frame.
        internal static void OnFrame()
        {
            bool pulled;
            lock (Gate) pulled = pull;
            if (!pulled && !Handoff.Attached) return;
            var tm = Find.TickManager;
            var map = Find.CurrentMap;
            if (tm == null || map == null) return;
            var tick = tm.TicksGame;
            var paused = tm.Paused;
            var w = Interlocked.Read(ref writes);
            // Deferred writes wait for a flush, or DeferredSeconds at most.
            var needed = Interlocked.Read(ref due);
            if (deferredAt != 0 && Stopwatch.GetTimestamp() - deferredAt >= DeferredSeconds * Stopwatch.Frequency) needed = w;
            var period = CombatMirror.Dirty ? CombatMirror.MinCombatCompareTicks : PeriodTicks;
            // A paused game whose tick moved since the last capture is a clock
            // window that ran and stopped between two frame hooks (a stop on a
            // combat event, a few ticks in): the stop's decision reads the stop
            // tick, so that frame is due at once, not a wall second later (#2826).
            var periodic = needed <= capturedWrites && paused == capturedPaused && tick >= capturedTick && !(paused && tick != capturedTick);
            // A paused game advances no tick, so the period is wall time
            // there: a change no native write made (a fixture op, a
            // player edit while paused) is otherwise never captured.
            if (periodic && (paused ? Stopwatch.GetTimestamp() - capturedAt < PausedPeriodSeconds * Stopwatch.Frequency : tick - capturedTick < period)) return;
            // A periodic frame waits out DutyCycle times the last capture's
            // cost, so the stream holds the game thread at most ~1/DutyCycle
            // of wall time (a capture is ~200 ms, a period at
            // Ultrafast ~150 ms). Writes and pause edges capture at once.
            if (periodic && Stopwatch.GetTimestamp() - capturedAt < (long)lastCaptureMicros * DutyCycle * Stopwatch.Frequency / 1_000_000) return;
            // One frame in flight at a time: a slow publish delays the next
            // capture instead of queueing stale ones.
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            RimGovernor.Brain.GameFacts.Observations.BundleSnapshot? frame;
            RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest shape;
            lock (Gate) shape = subscription;
            var captureStarted = Stopwatch.GetTimestamp();
            GameThreadTrace.Mark("snapshot capture");
            var account = ObservationWork.BeginCapture();
            try { frame = SnapshotFrames.Capture(map, shape); }
            catch (SnapshotFrames.SnapshotSectionException e)
            {
                // A section threw: the frame is the failure alone, so the
                // reader refuses its reads with the section named.
                frame = e.Frame();
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref pending, 0);
                ModLog.Warn("snapshot", "frame capture failed: " + e.Message);
                return;
            }
            finally { ObservationWork.End(); }
            GameThreadTrace.Mark("snapshot captured");
            var captureMicros = Micros(captureStarted);
            NoteSlowCapture(captureMicros, tick, account);
            Interlocked.Exchange(ref capturedWrites, w); capturedTick = tick; capturedPaused = paused;
            if (w == Interlocked.Read(ref writes)) deferredAt = 0;
            capturedAt = Stopwatch.GetTimestamp(); lastCaptureMicros = captureMicros;
            if (frame == null) { Interlocked.Exchange(ref pending, 0); return; }
            var captured = frame;
            Task.Factory.StartNew(() =>
            {
                try
                {
                    if (pulled)
                    {
                        lock (Gate) { pullFrame = captured; pullFrameWrites = w; pullHead++; }
                    }
                    if (Handoff.Attached) Handoff.Publish(new RimGovernor.Brain.Bridge.NativeFrame(captured, w, captureMicros));
                }
                catch (Exception e) { ModLog.Warn("snapshot", "frame publish failed: " + e.Message); }
                finally { Interlocked.Exchange(ref pending, 0); }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// A capture past this many microseconds of game thread is logged
        /// with its per-family breakdown, at most once per SlowLogTicks.
        internal const uint SlowCaptureMicros = 20_000;
        private const long DutyCycle = 10;
        private static long capturedAt;
        private static uint lastCaptureMicros;
        private const int SlowLogTicks = RimWorld.GenDate.TicksPerHour;
        private static int slowLoggedTick = -SlowLogTicks;

        // NoteSlowCapture logs where a slow frame capture's game-thread time
        // went, family by family, so the capture can be profiled from
        // the flight stream.
        private static void NoteSlowCapture(uint micros, int tick, ObservationWork.Hop? hop)
        {
            if (micros < SlowCaptureMicros || hop == null || (tick >= slowLoggedTick && tick - slowLoggedTick < SlowLogTicks)) return;
            slowLoggedTick = tick;
            var parts = new System.Text.StringBuilder();
            foreach (var s in hop.Sections.OrderByDescending(s => s.Ticks))
                parts.Append(' ').Append(s.Name).Append('=').Append((s.Ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("ms/").Append(s.Rows);
            ModLog.Info("snapshot", "frame capture " + (micros / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "ms at tick " + tick + ":" + parts);
        }

        // Elapsed microseconds since a Stopwatch timestamp, saturated.
        private static uint Micros(long started)
        {
            var micros = (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency;
            return (uint)Math.Min(Math.Max(micros, 0), uint.MaxValue);
        }
    }
}
