#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One resumable optional-observation job (#654): work split into small
    /// units the scheduler runs one at a time on the game thread, checking
    /// the frame's elapsed allowance and the job's validity between units.
    /// A job keeps its own cursor and never holds a live game enumerator
    /// across units.
    /// </summary>
    internal interface IObservationJob
    {
        /// Why the job can no longer finish (its map unloaded, its
        /// incarnation moved, the tick rewound), or null while it can. The
        /// scheduler checks it before every unit and discards an obsolete job.
        string? Obsolete { get; }

        /// Runs one unit; true once the job has finished (and published).
        bool Step();

        /// The job was discarded unfinished: expired, obsolete or superseded.
        void Abandon(string reason);
    }

    /// <summary>
    /// The shared per-frame allowance for optional observation work (#654).
    /// Every migrated job run in one frame, from the frame boundary or
    /// inline in a hop, charges the same account, so the allowance is per
    /// frame and never a fresh one per request. A unit is not preemptible:
    /// the account admits a unit while any allowance remains and records
    /// how far the last one overran it rather than pretending a stopwatch
    /// interrupts a native accessor. Pure over an injected monotonic clock
    /// so the probes drive frames and time directly.
    /// </summary>
    internal sealed class ObservationFrameBudget
    {
        /// The initial tuning point: 1.5 ms of optional capture per frame.
        internal const double DefaultAllowanceMs = 1.5;

        internal readonly Func<long> Clock;
        internal readonly long TicksPerSecond;
        internal long AllowanceTicks;

        private ulong frame;
        private long spent;

        // Session aggregates, for the observation timing block.
        internal ulong Frames, Units, DeferredFrames, OverrunFrames, ControlServiced;
        internal long AggregateTicks, MaxUnitTicks, MaxOverrunTicks, ControlTicks;

        internal ObservationFrameBudget(Func<long> clock, long ticksPerSecond, double allowanceMs)
        {
            Clock = clock; TicksPerSecond = ticksPerSecond;
            AllowanceTicks = Math.Max(0, (long)(allowanceMs * ticksPerSecond / 1000.0));
        }

        internal ulong Frame => frame;
        internal long SpentTicks => spent;

        /// The frame boundary: a new frame starts with the whole allowance.
        internal void BeginFrame(ulong next)
        {
            if (next == frame) return;
            if (spent > AllowanceTicks) OverrunFrames++;
            frame = next; spent = 0; Frames++;
        }

        /// Whether another unit may start this frame.
        internal bool HasRoom => spent < AllowanceTicks;

        /// One unit's elapsed ticks, charged to the frame.
        internal void Charge(long ticks)
        {
            if (ticks < 0) ticks = 0;
            spent += ticks; AggregateTicks += ticks; Units++;
            if (ticks > MaxUnitTicks) MaxUnitTicks = ticks;
            if (spent > AllowanceTicks && spent - AllowanceTicks > MaxOverrunTicks) MaxOverrunTicks = spent - AllowanceTicks;
        }

        /// Required control work run at an optional yield point: its own
        /// account, never charged to the optional allowance.
        internal void ChargeControl(int hops, long ticks)
        {
            ControlServiced += (ulong)hops; ControlTicks += Math.Max(0, ticks);
        }

        internal double Ms(long ticks) => ticks * 1000.0 / TicksPerSecond;

        internal Dictionary<string, object?> Report(int pending, ulong expired, ulong abandoned, ulong saturated) => new Dictionary<string, object?>
        {
            ["allowanceMs"] = Ms(AllowanceTicks),
            ["frames"] = Frames,
            ["units"] = Units,
            ["aggregateMs"] = Ms(AggregateTicks),
            ["maxUnitMs"] = Ms(MaxUnitTicks),
            ["maxOverrunMs"] = Ms(MaxOverrunTicks),
            ["overrunFrames"] = OverrunFrames,
            ["deferredFrames"] = DeferredFrames,
            ["controlServiced"] = ControlServiced,
            ["controlMs"] = Ms(ControlTicks),
            ["pendingJobs"] = pending,
            ["expired"] = expired,
            ["abandoned"] = abandoned,
            ["saturated"] = saturated,
        };
    }

    /// <summary>
    /// The bounded queue of optional observation jobs (#654), run on the
    /// game thread from the real frame boundary (ObservationFrameHook) and
    /// inline from a hop that wants its result now. Before every optional
    /// unit at the frame boundary, queued control hops run first (their cost
    /// accounted apart); inline in a hop, a queued control hop ends the
    /// hop's quanta instead, so the next pump serves it. Jobs share one
    /// allowance per frame, run round-robin, and each gets at least one unit
    /// per frame from the boundary so none starves; a job that has not
    /// finished within DeadlineFrames is abandoned, so its callers see an
    /// explicit pending/unavailable status instead of waiting forever.
    /// </summary>
    internal sealed class ObservationScheduler
    {
        internal const int MaxJobs = 4;
        internal const ulong DeadlineFrames = 600;

        internal readonly ObservationFrameBudget Budget;
        private readonly Func<int> serviceControl;
        private readonly Func<bool> controlPending;
        private readonly List<Entry> jobs = new List<Entry>();
        private int cursor;
        internal ulong Expired, Abandoned, Saturated;

        private sealed class Entry
        {
            internal readonly IObservationJob Job; internal readonly ulong Started;
            internal Entry(IObservationJob job, ulong started) { Job = job; Started = started; }
        }

        /// serviceControl runs every queued control hop and returns how
        /// many ran; controlPending says whether one is queued.
        internal ObservationScheduler(ObservationFrameBudget budget, Func<int> serviceControl, Func<bool> controlPending)
        {
            Budget = budget; this.serviceControl = serviceControl; this.controlPending = controlPending;
        }

        internal int Pending => jobs.Count;

        internal bool Contains(IObservationJob job) => jobs.Exists(e => e.Job == job);

        /// Queues job; false when the queue is full (the caller reports
        /// saturation and keeps whatever valid view it holds).
        internal bool TryAdd(IObservationJob job)
        {
            if (Contains(job)) return true;
            if (jobs.Count >= MaxJobs) { Saturated++; return false; }
            jobs.Add(new Entry(job, Budget.Frame));
            return true;
        }

        /// Discards job unfinished (a newer request superseded it).
        internal void Remove(IObservationJob job, string reason)
        {
            var i = jobs.FindIndex(e => e.Job == job);
            if (i < 0) return;
            jobs.RemoveAt(i); Abandoned++;
            job.Abandon(reason);
        }

        /// The frame boundary: a new allowance, then every job's quanta
        /// within it, control first before each.
        internal void RunFrame(ulong frame)
        {
            Budget.BeginFrame(frame);
            if (jobs.Count == 0) return;
            var floor = true;
            while (jobs.Count > 0 && (Budget.HasRoom || floor))
            {
                ServiceControl();
                if (!RunOne(null)) continue;
                floor = false;
            }
            if (jobs.Count > 0) Budget.DeferredFrames++;
            Expire();
        }

        /// Inline, from a hop on the game thread: job's units while the
        /// frame's allowance lasts, no control hop is waiting and the
        /// caller has not cancelled. True when job left the queue (finished
        /// or discarded).
        internal bool RunInline(IObservationJob job, Func<bool> cancelled)
        {
            while (Contains(job))
            {
                if (!Budget.HasRoom || cancelled() || controlPending()) return false;
                RunOne(job);
            }
            return true;
        }

        private void ServiceControl()
        {
            if (!controlPending()) return;
            var began = Budget.Clock();
            var hops = serviceControl();
            Budget.ChargeControl(hops, Budget.Clock() - began);
        }

        // One unit of only (or the next job round-robin); false when the
        // chosen job was obsolete and discarded without running.
        private bool RunOne(IObservationJob? only)
        {
            int i;
            if (only != null) i = jobs.FindIndex(e => e.Job == only);
            else { if (cursor >= jobs.Count) cursor = 0; i = cursor++; }
            if (i < 0) return false;
            var job = jobs[i].Job;
            var obsolete = job.Obsolete;
            if (obsolete != null) { jobs.RemoveAt(i); Abandoned++; job.Abandon(obsolete); return false; }
            var began = Budget.Clock();
            bool done;
            try { done = job.Step(); }
            catch (Exception e) { done = true; jobs.RemoveAt(i); Abandoned++; job.Abandon("failed: " + e.GetType().Name); Budget.Charge(Budget.Clock() - began); return true; }
            Budget.Charge(Budget.Clock() - began);
            if (done) jobs.Remove(jobs.Find(e => e.Job == job)!);
            return true;
        }

        private void Expire()
        {
            for (var i = jobs.Count - 1; i >= 0; i--)
            {
                if (Budget.Frame - jobs[i].Started < DeadlineFrames) continue;
                var job = jobs[i].Job;
                jobs.RemoveAt(i); Expired++;
                job.Abandon("expired");
            }
        }

        internal Dictionary<string, object?> Report() => Budget.Report(jobs.Count, Expired, Abandoned, Saturated);
    }

    /// <summary>
    /// The process's one optional-observation scheduler (#654), driven from
    /// ObservationFrameHook's frame boundary. The allowance is
    /// RIMGOVERNOR_OBSERVATION_BUDGET_MS when set to a number in [0.1, 50],
    /// else DefaultAllowanceMs.
    /// </summary>
    internal static class ObservationScheduling
    {
        internal const string AllowanceVariable = "RIMGOVERNOR_OBSERVATION_BUDGET_MS";

        internal static readonly ObservationScheduler Shared = new ObservationScheduler(
            new ObservationFrameBudget(Stopwatch.GetTimestamp, Stopwatch.Frequency, Allowance(Environment.GetEnvironmentVariable(AllowanceVariable))),
            MainThreadAdmission.RunControl, MainThreadAdmission.ControlPending);

        internal static double Allowance(string? configured)
            => double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0.1 && ms <= 50 ? ms : ObservationFrameBudget.DefaultAllowanceMs;

        /// The frame boundary, on the game thread.
        internal static void Frame() => Shared.RunFrame(FrameAccounting.OpenFrame());

        /// Before an inline quantum in a hop: the hop's frame, so a hop
        /// shares the allowance the boundary opened for it.
        /// Without the frame hook no boundary ever opens a frame, so each
        /// hop opens its own rather than inheriting a spent one.
        internal static void Join() => Shared.Budget.BeginFrame(FrameAccounting.Hooked ? FrameAccounting.OpenFrame() : Shared.Budget.Frame + 1);
    }
}
