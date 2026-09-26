using System;
using System.Collections.Generic;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

// Frame-budgeted resumable capture (#654), over a fake frame source and a
// controllable monotonic clock: the per-frame allowance is shared by every
// job and admits at most one in-progress unit past it, the rest runs on
// later frames, a queued control request runs before the next optional
// quantum, and cancellation, obsolescence, saturation, expiry and failure
// each leave the queue bounded. The resumable refresh is then driven one
// band per frame with mutations, an unload and a resync burst in between.
internal static partial class NativePlanningWindowViewProbe
{
    private static Obs.PlanningWindowView ServeRoot(PlanningWindowViewPublisher publisher, PlanningViewRoot root, Common.ObservationContext context)
        => PlanningWindowViewProjection.Serve(publisher, root, null, false, new Obs.BundlePlanningWindowViewRequest(), context);

    // One clock tick is a microsecond: 1000 per millisecond.
    private sealed class FakeClock { internal long Now; }

    private sealed class FakeJob : IObservationJob
    {
        private readonly FakeClock clock; private readonly List<string> log; private readonly string name;
        internal int Units, Remaining; internal long Cost; internal string ObsoleteReason, Abandoned; internal Action OnStep; internal bool Throw;
        internal FakeJob(string name, FakeClock clock, List<string> log, int units, long cost)
        { this.name = name; this.clock = clock; this.log = log; Remaining = units; Cost = cost; }
        public string Obsolete => ObsoleteReason;
        public bool Step()
        {
            clock.Now += Cost; Units++; Remaining--; log.Add(name);
            OnStep?.Invoke();
            if (Throw) throw new InvalidOperationException("unit failed");
            return Remaining <= 0;
        }
        public void Abandon(string reason) { Abandoned = reason; }
    }

    private sealed class Rig
    {
        internal readonly FakeClock Clock = new FakeClock();
        internal readonly List<string> Log = new List<string>();
        internal readonly Queue<string> Control = new Queue<string>();
        internal readonly ObservationScheduler Scheduler;
        internal ulong Frame;
        internal Rig(double allowanceMs)
        {
            var budget = new ObservationFrameBudget(() => Clock.Now, 1000000, allowanceMs);
            Scheduler = new ObservationScheduler(budget, () =>
            {
                var n = 0;
                while (Control.Count > 0) { Log.Add("control:" + Control.Dequeue()); Clock.Now += 50; n++; }
                return n;
            }, () => Control.Count > 0);
        }
        internal FakeJob Job(string name, int units, long cost) { var job = new FakeJob(name, Clock, Log, units, cost); Check(Scheduler.TryAdd(job), name + " queued"); return job; }
        internal void RunFrame() => Scheduler.RunFrame(++Frame);
    }

    private static void BudgetSpreadsCaptureAcrossFrames()
    {
        AllowanceIsSharedPerFrame();
        ControlRunsBeforeTheNextQuantum();
        InlineQuantaYieldAndCancelAlone();
        StarvedJobsStillProgressOrExpire();
        ObsoleteFailedAndSaturatedStayBounded();
        ResumableRefreshAcrossFrames();
        AllowanceConfiguration();
    }

    // Three bulk jobs, 400 us a unit, a 1 ms allowance: one frame runs
    // units until the shared account is spent, overrunning by at most the
    // one unit in progress; the rest waits for later frames.
    private static void AllowanceIsSharedPerFrame()
    {
        var rig = new Rig(1.0);
        var jobs = new[] { rig.Job("a", 5, 400), rig.Job("b", 5, 400), rig.Job("c", 5, 400) };
        rig.RunFrame();
        var budget = rig.Scheduler.Budget;
        Check(budget.Units == 3 && budget.SpentTicks == 1200, "one frame: units until the shared allowance is spent (" + budget.Units + ")");
        Check(budget.SpentTicks - budget.AllowanceTicks <= 400 && budget.MaxOverrunTicks == 200, "the overrun is at most the in-progress unit");
        Check(jobs[0].Units == 1 && jobs[1].Units == 1 && jobs[2].Units == 1, "round-robin: every job advanced once");
        Check(rig.Scheduler.Pending == 3 && budget.DeferredFrames == 1, "the remaining work is deferred to a later frame");
        // The same frame again (a hop inside it) gets no fresh allowance.
        budget.BeginFrame(rig.Frame);
        Check(!budget.HasRoom, "a second pass in the same frame shares the spent allowance");
        var frames = 1;
        while (rig.Scheduler.Pending > 0 && frames < 20) { rig.RunFrame(); frames++; }
        Check(rig.Scheduler.Pending == 0 && frames == 5, "15 units at three per frame finish on the fifth frame (" + frames + ")");
        Check(budget.Units == 15 && budget.MaxUnitTicks == 400 && Math.Abs(budget.Ms(budget.AggregateTicks) - 6.0) < 1e-9, "aggregate and max-unit time reported");
        var report = rig.Scheduler.Report();
        Check((double)report["allowanceMs"] == 1.0 && (double)report["maxUnitMs"] == 0.4 && (ulong)report["units"] == 15, "the report carries the allowance and unit timings");
    }

    // A control request queued while an optional unit runs executes before
    // the next optional quantum, and its cost is accounted apart.
    private static void ControlRunsBeforeTheNextQuantum()
    {
        var rig = new Rig(5.0);
        var bulk = rig.Job("bulk", 4, 300);
        bulk.OnStep = () => { if (bulk.Units == 1) rig.Control.Enqueue("renew"); };
        rig.RunFrame();
        Check(rig.Log.Count >= 3 && rig.Log[0] == "bulk" && rig.Log[1] == "control:renew" && rig.Log[2] == "bulk", "control ran before the next optional quantum: " + string.Join(",", rig.Log));
        var budget = rig.Scheduler.Budget;
        Check(budget.ControlServiced == 1 && budget.ControlTicks == 50, "control cost accounted apart");
        Check(budget.AggregateTicks == 1200, "control never charged to the optional allowance");
    }

    // Inline in a hop: quanta stop for a queued control hop or the caller's
    // cancellation; the job stays queued for the other subscribers.
    private static void InlineQuantaYieldAndCancelAlone()
    {
        var rig = new Rig(10.0);
        var job = rig.Job("view", 6, 100);
        rig.Scheduler.Budget.BeginFrame(++rig.Frame);
        Check(!rig.Scheduler.RunInline(job, () => true) && job.Units == 0, "cancelled before the first unit: nothing ran");
        var cancelled = false;
        job.OnStep = () => { if (job.Units == 2) cancelled = true; };
        Check(!rig.Scheduler.RunInline(job, () => cancelled) && job.Units == 2, "cancelled between units: the caller stops");
        Check(rig.Scheduler.Contains(job) && job.Abandoned == null, "one caller cancelling leaves the job for the others");
        job.OnStep = () => { if (job.Units == 3) rig.Control.Enqueue("stop"); };
        Check(!rig.Scheduler.RunInline(job, () => false) && job.Units == 3 && rig.Control.Count == 1, "a queued control hop ends the inline quanta");
        rig.Control.Clear();
        job.OnStep = null;
        Check(rig.Scheduler.RunInline(job, () => false) && job.Remaining == 0 && !rig.Scheduler.Contains(job), "another subscriber finishes it inline");
    }

    // A zero allowance still gives each frame one unit (the progress
    // floor); a job that cannot finish by its deadline is abandoned
    // explicitly rather than pending forever.
    private static void StarvedJobsStillProgressOrExpire()
    {
        var rig = new Rig(1.0);
        rig.Scheduler.Budget.AllowanceTicks = 0;
        var job = rig.Job("slow", 3, 10);
        rig.RunFrame(); rig.RunFrame();
        Check(job.Units == 2, "one unit per frame with the allowance exhausted");
        rig.RunFrame();
        Check(rig.Scheduler.Pending == 0 && job.Remaining == 0, "the starved job finished");
        var forever = rig.Job("forever", int.MaxValue, 10);
        for (var i = 0; i < (int)ObservationScheduler.DeadlineFrames + 1 && rig.Scheduler.Pending > 0; i++) rig.RunFrame();
        Check(forever.Abandoned == "expired" && rig.Scheduler.Pending == 0 && rig.Scheduler.Expired == 1, "an unfinished job expires at its deadline");
    }

    private static void ObsoleteFailedAndSaturatedStayBounded()
    {
        var rig = new Rig(10.0);
        var unloaded = rig.Job("unloaded", 5, 10);
        unloaded.OnStep = () => unloaded.ObsoleteReason = "unloaded";
        var failing = rig.Job("failing", 5, 10);
        failing.Throw = true;
        rig.Job("c", 1000, 100); rig.Job("d", 1000, 100);
        Check(!rig.Scheduler.TryAdd(new FakeJob("e", rig.Clock, rig.Log, 1, 1)) && rig.Scheduler.Saturated == 1 && rig.Scheduler.Pending == ObservationScheduler.MaxJobs,
            "the queue is bounded: a fifth job is refused as saturated");
        rig.RunFrame();
        Check(unloaded.Units == 1 && unloaded.Abandoned == "unloaded", "an obsolete job is discarded before its next unit");
        Check(failing.Units == 1 && failing.Abandoned == "failed: InvalidOperationException", "a failed unit discards its job");
        Check(rig.Scheduler.Pending == 2 && rig.Scheduler.Abandoned == 2, "only the live jobs remain");
        var superseded = rig.Scheduler;
        var last = new FakeJob("f", rig.Clock, rig.Log, 1, 1);
        Check(superseded.TryAdd(last), "room again after discards");
        superseded.Remove(last, "superseded");
        Check(last.Abandoned == "superseded" && !superseded.Contains(last), "a superseded job is removed and told why");
    }

    // The refresh one band per frame: a mutation between steps is caught
    // by the band still to come immediately and by a finished band on the
    // next refresh; an unload mid-capture refuses its publication; a burst
    // past the dirty list resyncs.
    private static void ResumableRefreshAcrossFrames()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var identity = Identity("load");
        var stats = new PlanningViewRefreshStats();
        var job = new PlanningViewRefreshJob(ledger, publisher.Begin(identity), null, identity, 1, MapSize, MapSize, RMinX, RMinZ, RMaxX, RMaxZ, 100, world, stats);
        Check(job.Bands == 3 && !job.Step(100) && job.Root == null && world.Reads == 20 * 8, "bootstrap: one band per unit");
        world.Set(ledger, 12, 18, Cell("done-band"));
        world.Set(ledger, 12, 36, Cell("later-band"));
        Check(!job.Step(101) && job.Step(102) && job.Root != null && job.Root.Complete, "the remaining bands finish on later frames");
        var root = job.Root;
        Check(root.PublishedTick == 102 && root.Chunk(0).CapturedTick == 100 && root.Chunk(2).CapturedTick == 102, "each chunk carries the tick it was read at");
        Check(root.Chunk(2)[(36 - 32) * 20 + 2].ZoneId == "later-band" && root.Chunk(0)[2 * 20 + 2].ZoneId != "done-band", "a later band sees the edit; a finished band holds its read");
        Check(publisher.TryPublish(root), "the finished root publishes");
        var next = Refresh(publisher, ledger, world, 103, out var nextStats);
        Check(nextStats.Rebuilt == 1 && nextStats.DirtyChunks == 1 && Current(next, world), "the next refresh rebuilds only the band finished before the edit and matches a full read");

        var unloading = new PlanningViewRefreshJob(ledger, publisher.Begin(identity), publisher.Acquire(), identity, 1, MapSize, MapSize, RMinX, RMinZ, RMaxX, RMaxZ, 200, world, new PlanningViewRefreshStats());
        unloading.Step(200);
        publisher.Invalidate();
        unloading.Step(201); unloading.Step(202);
        Check(unloading.Root != null && !publisher.TryPublish(unloading.Root) && publisher.Acquire() == null, "a capture spanning an unload never publishes");

        var rewound = new PlanningViewRefreshJob(ledger, publisher.Begin(identity), null, identity, 1, MapSize, MapSize, RMinX, RMinZ, RMaxX, RMaxZ, 300, world, new PlanningViewRefreshStats());
        rewound.Step(300);
        var threw = false;
        try { rewound.Step(299); } catch (InvalidOperationException) { threw = true; }
        Check(threw && rewound.Root == null, "a rewound tick mid-capture refuses the step");

        var bootstrap = Refresh(publisher, ledger, world, 400, out _);
        for (var x = 0; x < MapSize; x++)
            for (var z = 0; z < MapSize; z++) world.Set(ledger, x, z, Cell("burst"));
        var burstStats = new PlanningViewRefreshStats();
        var burst = new PlanningViewRefreshJob(ledger, publisher.Begin(identity), bootstrap, identity, 1, MapSize, MapSize, RMinX, RMinZ, RMaxX, RMaxZ, 401, world, burstStats);
        var steps = 1;
        while (!burst.Step(401 + steps)) steps++;
        Check(steps == 3 && burstStats.Rebuilt == 3 && burstStats.DirtyChunks == 3 && Current(burst.Root, world), "a map-wide burst rebuilds every band, one per frame, to parity");
        Check(burstStats.RetainedBytes == 20L * 24 * PlanningViewRefresh.CellBytes, "retained bytes stay the region's");
    }

    private static void AllowanceConfiguration()
    {
        Check(ObservationScheduling.Allowance(null) == ObservationFrameBudget.DefaultAllowanceMs, "default allowance");
        Check(ObservationScheduling.Allowance("2") == 2.0 && ObservationScheduling.Allowance("0.01") == ObservationFrameBudget.DefaultAllowanceMs
            && ObservationScheduling.Allowance("x") == ObservationFrameBudget.DefaultAllowanceMs, "configured allowance, bounded");
    }
}
