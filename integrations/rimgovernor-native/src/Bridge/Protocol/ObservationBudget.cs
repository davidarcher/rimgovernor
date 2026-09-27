#nullable enable
using System;
using System.Collections.Generic;

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
    /// The bounded queue of optional observation jobs (#654), run on the
    /// game thread from MainThreadAdmission's frame boundary, after the
    /// frame's required hops, and inline from a hop that wants its result
    /// now. Optional units charge the same per-frame allowance hops do
    /// (#995), so optional work only uses what the hops left. Before every
    /// optional unit at the boundary, queued control hops run first (charged
    /// like any hop); inline in a hop, a queued control hop ends the hop's
    /// quanta instead, so the next pump serves it. Jobs run round-robin, and
    /// each frame the boundary runs at least one unit so none starves; a job
    /// that has not finished within DeadlineFrames is abandoned, so its
    /// callers see an explicit pending/unavailable status instead of
    /// waiting forever.
    /// </summary>
    internal sealed class ObservationScheduler
    {
        internal const int MaxJobs = 4;
        internal const ulong DeadlineFrames = 600;

        private readonly List<Entry> jobs = new List<Entry>();
        private int cursor;
        internal ulong Expired, Abandoned, Saturated, Units, DeferredFrames;
        internal long AggregateTicks, MaxUnitTicks;

        private sealed class Entry
        {
            internal readonly IObservationJob Job; internal readonly ulong Started;
            internal Entry(IObservationJob job, ulong started) { Job = job; Started = started; }
        }

        internal int Pending => jobs.Count;

        internal bool Contains(IObservationJob job) => jobs.Exists(e => e.Job == job);

        /// Queues job; false when the queue is full (the caller reports
        /// saturation and keeps whatever valid view it holds).
        internal bool TryAdd(IObservationJob job)
        {
            if (Contains(job)) return true;
            if (jobs.Count >= MaxJobs) { Saturated++; return false; }
            jobs.Add(new Entry(job, MainThreadAdmission.FrameIndex));
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

        /// The frame boundary, after the frame's hops: every job's quanta
        /// within what the allowance has left, control first before each.
        internal void RunFrame()
        {
            if (jobs.Count == 0) return;
            var floor = true;
            while (jobs.Count > 0 && (MainThreadAdmission.HasRoom || floor))
            {
                MainThreadAdmission.RunControl();
                if (!RunOne(null)) continue;
                floor = false;
            }
            if (jobs.Count > 0) DeferredFrames++;
            Expire();
        }

        /// Inline, from a hop on the game thread: job's units while the
        /// frame's allowance lasts, no control hop is waiting and the
        /// caller has not cancelled. True when job left the queue (finished
        /// or discarded).
        internal bool RunInline(IObservationJob job, Func<bool> cancelled)
        {
            MainThreadAdmission.JoinFrame();
            while (Contains(job))
            {
                if (!MainThreadAdmission.HasRoom || cancelled() || MainThreadAdmission.ControlPending()) return false;
                RunOne(job);
            }
            return true;
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
            var began = MainThreadAdmission.Clock();
            bool done;
            try { done = job.Step(); }
            catch (Exception e) { done = true; jobs.RemoveAt(i); Abandoned++; job.Abandon("failed: " + e.GetType().Name); Charge(began); return true; }
            Charge(began);
            if (done) jobs.Remove(jobs.Find(e => e.Job == job)!);
            return true;
        }

        private void Charge(long began)
        {
            var ticks = Math.Max(0, MainThreadAdmission.Clock() - began);
            MainThreadAdmission.Charge(ticks);
            Units++; AggregateTicks += ticks;
            if (ticks > MaxUnitTicks) MaxUnitTicks = ticks;
        }

        private void Expire()
        {
            for (var i = jobs.Count - 1; i >= 0; i--)
            {
                if (MainThreadAdmission.FrameIndex - jobs[i].Started < DeadlineFrames) continue;
                var job = jobs[i].Job;
                jobs.RemoveAt(i); Expired++;
                job.Abandon("expired");
            }
        }

        /// The optional-job fields of the one allowance report.
        internal void Report(Dictionary<string, object?> into)
        {
            if (Units == 0) return;
            into["optionalUnits"] = Units;
            into["optionalMs"] = MainThreadAdmission.Ms(AggregateTicks);
            into["maxUnitMs"] = MainThreadAdmission.Ms(MaxUnitTicks);
            into["optionalDeferredFrames"] = DeferredFrames;
            into["pendingJobs"] = jobs.Count;
            into["expired"] = Expired;
            into["abandoned"] = Abandoned;
            into["saturated"] = Saturated;
        }
    }

    /// <summary>
    /// The process's one optional-observation scheduler (#654), driven from
    /// MainThreadAdmission's frame boundary.
    /// </summary>
    internal static class ObservationScheduling
    {
        internal static ObservationScheduler Shared = new ObservationScheduler();
    }
}
