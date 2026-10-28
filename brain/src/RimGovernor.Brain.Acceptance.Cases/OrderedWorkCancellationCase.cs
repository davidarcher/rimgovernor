using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameOrders.Operations;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Exact-identity cancellation against actual saved native jobs; about a minute including startup.</summary>
internal static class OrderedWorkCancellationCase
{
    private static readonly string[] TargetNames = ["first", "second", "third"];
    private static readonly FixtureOp<(string Pawn, string[] Targets), Fx.OrderedWorkLifecycleRead> Prepare = new(
        "test/ordered_work_prepare", a => ServedOps.Args(("pawn", a.Pawn), ("first", a.Targets[0]), ("second", a.Targets[1]), ("third", a.Targets[2])),
        ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_prepare"));
    private static readonly FixtureOp<NoArgs, Fx.OrderedWorkLifecycleRead> Read = new(
        "test/ordered_work_read", _ => ServedOps.Args(), ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_read"));
    private static readonly FixtureOp<bool, Fx.OrderedWorkLifecycleRead> Foreign = new(
        "test/ordered_work_foreign", replace => ServedOps.Args(("replace", replace)), ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_foreign"));

    internal static TypedCase Case(bool replaced = false) => new()
    {
        Name = replaced ? "control/ordered-work-replaced-reload" : "control/ordered-work-cancel-reload",
        Scope = "After real partial completion and save/load, cancellation removes only remaining intervention identities and preserves unrelated "
            + (replaced ? "current work; the replaced job is observed gone." : "queued work; completed work remains completed."),
        Ticks = 3000, Tier = CaseTier.OnDemand, Start = new LabStart { Colonists = 1 }, Crew = new() { Size = 1 },
        QuietWorld = true, NoKeep = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new() { Width = 31, Height = 5, OffsetX = 3, Radius = 50, Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged] },
            Clear = [new() { Rect = new(0, 0, 31, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
            Buildings = [new() { Name = "first", Def = "Wall", Stuff = "WoodLog", Cells = [new(2, 2)] },
                new() { Name = "second", Def = "Wall", Stuff = "WoodLog", Cells = [new(16, 2)] },
                new() { Name = "third", Def = "Wall", Stuff = "WoodLog", Cells = [new(28, 2)] }],
            Pawns = [new() { Name = "worker", Colonist = 0, Cell = new(1, 2) }],
        },
        Ops = [Prepare.Tool, Read.Tool, Foreign.Tool],
        Run = (s, ct) => RunAsync(s, replaced, ct),
    };

    private static async Task RunAsync(TypedSession s, bool replaced, CancellationToken ct)
    {
        var stage = s.Staged!;
        var worker = stage.Pawns["worker"].Id;
        var targets = TargetNames.Select(n => stage.Buildings[n].Ids.Single()).ToArray();
        var prepared = await s.FixtureAsync("prepare", Prepare, (worker, targets), ct).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
        var request = new OrderedWork(new(worker), 1, "remove staged obstructions",
            EquatableArray.From(targets.Select(t => new OrderedWorkStep(prepared.JobDef, t))));
        _ = await TypedApply.AppliedAsync(s, "install", new GiveJobIntent { Queue = request }, ct).ConfigureAwait(false);
        Fx.OrderedWorkLifecycleRead last = new();
        _ = await s.RunUntilAsync("first-completed", s.TicksLeft, async call =>
        {
            last = await s.FixtureAsync("read", Read, NoArgs.Value, call).ConfigureAwait(false);
            if (last.Outcomes.Any(o => o is "Gone" or "Failed")) throw new AcceptanceException("Queue failed before cancellation: " + ServedOps.Json(last));
            return (string.Join(",", last.Outcomes), last.Outcomes.FirstOrDefault() == "Succeeded");
        }, ct).ConfigureAwait(false);
        if (last.Outcomes.Length != 3 || last.Outcomes[1] != "Running" || last.Outcomes[2] != "Queued")
            throw new AcceptanceException("Expected a completed, running and queued job: " + ServedOps.Json(last));
        var before = await s.FixtureAsync("foreign", Foreign, replaced, ct).ConfigureAwait(false);
        await s.ReloadAsync("partial-queue", ct).ConfigureAwait(false);
        var loaded = await s.FixtureAsync("loaded", Read, NoArgs.Value, ct).ConfigureAwait(false);
        if (!loaded.IntendedJobs.SequenceEqual(before.IntendedJobs) || !loaded.Outcomes.SequenceEqual(before.Outcomes)
            || loaded.CurrentJob != before.CurrentJob || !loaded.QueuedJobs.SequenceEqual(before.QueuedJobs))
            throw new AcceptanceException("Save/load changed queue identities or outcomes: " + ServedOps.Json(loaded));
        await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
        var observed = (await TypedApply.PawnAsync(s, "loaded-pawn", worker, ct).ConfigureAwait(false)).Job.OrderedWork;
        if (observed?.Request != request) throw new AcceptanceException("Loaded pawn lost immutable queue provenance.");
        _ = await TypedApply.AppliedAsync(s, "cancel", new GiveJobIntent { Queue = request with { Cancel = true } }, ct).ConfigureAwait(false);
        var cancelled = await s.FixtureAsync("cancelled", Read, NoArgs.Value, ct).ConfigureAwait(false);
        var expected = new[] { "Succeeded", replaced ? "Gone" : "Cancelled", "Cancelled" };
        if (!cancelled.Outcomes.SequenceEqual(expected) || cancelled.CurrentJob != before.ForeignJob
            || cancelled.QueuedJobs.Any(id => cancelled.IntendedJobs.Contains(id)))
            throw new AcceptanceException("Cancellation touched unrelated work or lost outcomes: " + ServedOps.Json(cancelled));
        _ = await TypedApply.AppliedAsync(s, "cancel-repeat", new GiveJobIntent { Queue = request with { Cancel = true } }, ct).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "install-repeat", new GiveJobIntent { Queue = request }, ct).ConfigureAwait(false);
        var repeated = await s.FixtureAsync("repeated", Read, NoArgs.Value, ct).ConfigureAwait(false);
        if (!repeated.Outcomes.SequenceEqual(expected) || repeated.CurrentJob != cancelled.CurrentJob
            || !repeated.QueuedJobs.SequenceEqual(cancelled.QueuedJobs))
            throw new AcceptanceException("Replay changed execution after cancellation: " + ServedOps.Json(repeated));
        s.Record("before_reload", ServedOps.Json(before)); s.Record("loaded", ServedOps.Json(loaded)); s.Record("cancelled", ServedOps.Json(cancelled));
    }
}
