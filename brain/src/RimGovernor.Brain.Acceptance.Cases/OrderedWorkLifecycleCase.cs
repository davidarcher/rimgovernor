using Fx = RimGovernor.Fixtures.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>One finite native queue lifecycle, expected about one minute including startup.</summary>
internal static class OrderedWorkLifecycleCase
{
    private static readonly FixtureOp<(string Pawn, string First, string Second), Fx.OrderedWorkLifecycleRead> Prepare = new(
        "test/ordered_work_prepare", a => ServedOps.Args(("pawn", a.Pawn), ("first", a.First), ("second", a.Second)),
        ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_prepare"));
    private static readonly FixtureOp<NoArgs, Fx.OrderedWorkLifecycleRead> Admit = new(
        "test/ordered_work_admit", _ => ServedOps.Args(), ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_admit"));
    private static readonly FixtureOp<NoArgs, Fx.OrderedWorkLifecycleRead> Read = new(
        "test/ordered_work_read", _ => ServedOps.Args(), ServedOps.Reply<Fx.OrderedWorkLifecycleRead>("test/ordered_work_read"));

    internal static TypedCase Case() => new()
    {
        Name = "control/ordered-work-lifecycle",
        Scope = "A staged intervention admitted through journal/Hands runs two native forced jobs in their intended order, removes both targets and returns to routine native selection. No planner justification claim.",
        Ticks = 3000, Tier = CaseTier.OnDemand, Start = new LabStart { Colonists = 1 }, Crew = new() { Size = 1 },
        QuietWorld = true, NoKeep = true, Keep = ["Food"],
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new() { Width = 9, Height = 5, OffsetX = 3, Radius = 30, Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged] },
            Clear = [new() { Rect = new(0, 0, 9, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
            Buildings = [new() { Name = "first", Def = "Wall", Stuff = "WoodLog", Cells = [new(2, 2)] },
                new() { Name = "second", Def = "Wall", Stuff = "WoodLog", Cells = [new(6, 2)] }],
            Things = [new() { Name = "meals", Def = "MealSimple", Count = 8, Cell = new(1, 1) }],
            Pawns = [new() { Name = "worker", Colonist = 0, Cell = new(1, 2) }],
        },
        Ops = [Prepare.Tool, Admit.Tool, Read.Tool],
        Serve = new() { Families = ["work"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "ordered-work" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken ct)
    {
        var staged = s.Staged!;
        var last = await s.FixtureAsync("prepare", Prepare,
            (staged.Pawns["worker"].Id, staged.Buildings["first"].Ids.Single(), staged.Buildings["second"].Ids.Single()), ct).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("queue-and-return", s.TicksLeft, async call =>
        {
            if (!last.Admitted) last = await s.FixtureAsync("admit", Admit, NoArgs.Value, call).ConfigureAwait(false);
            last = await s.FixtureAsync("observe", Read, NoArgs.Value, call).ConfigureAwait(false);
            if (last.Outcomes.Any(o => o is "Failed" or "Gone" or "Cancelled"))
                throw new AcceptanceException("Queue lost work: " + ServedOps.Json(last));
            if (last.ReturnedToNative)
                await VerifyReadbackAsync(s, staged.Pawns["worker"].Id, last.IntendedJobs, call).ConfigureAwait(false);
            return (Wait.Signature(last.Admitted, string.Join(",", last.Outcomes), last.TargetsRemoved, last.ReturnedToNative), last.ReturnedToNative);
        }, ct).ConfigureAwait(false);
        s.Record("queue", ServedOps.Json(last));
        if (!last.TargetsRemoved || !last.ReturnedToNative || last.IntendedJobs.Length != 2
            || !last.IntendedJobs.SequenceEqual(last.StartedJobs) || last.Outcomes.Any(o => o != "Succeeded"))
            throw new AcceptanceException("Finite native lifecycle not established: " + ServedOps.Json(last));
    }

    private static async Task VerifyReadbackAsync(TypedSession s, string worker, int[] intended, CancellationToken ct)
    {
        var pawn = await TypedApply.PawnAsync(s, "queue-readback", worker, ct).ConfigureAwait(false);
        var queue = pawn.Job.OrderedWork ?? throw new AcceptanceException("Pawn readback lost queue provenance.");
        if (!queue.Jobs.Select(j => j.JobId).SequenceEqual(intended)
            || queue.Jobs.Any(j => j.Status != OrderedWorkStatus.Succeeded))
            throw new AcceptanceException("Pawn readback disagrees with actual queue completion.");
        // The physical claim is complete. A fresh action key must still read back rather than reissue it.
        var replay = await TypedApply.AppliedAsync(s, "queue-repeat", new RimGovernor.Brain.GameOrders.Operations.GiveJobIntent { Queue = queue.Request }, ct).ConfigureAwait(false);
        var receipt = replay.Applied!.Outcome;
        var observed = receipt?.Applied?.Observed
            ?? (receipt as RimGovernor.Brain.GameFacts.Receipts.ReceiptOutcome.NoChangeCase)?.Value.Observed;
        if (observed?.Effect?.Job is not { Issued: false, Queue: { } repeated } || repeated != queue)
            throw new AcceptanceException("Completed queue was reissued or lost its receipt identity.");
    }
}
