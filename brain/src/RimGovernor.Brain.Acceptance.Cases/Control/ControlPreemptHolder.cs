using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/coupled-preempts-vanilla-holder</c> (#3671): a coupled order outranks vanilla work. An unowned colonist's vanilla
/// haul reserves a slate chunk; a Hands Deliver step for a second colonist on the same chunk ends that haul and completes,
/// where it used to fault <c>target_unreachable_or_reserved</c>. The native reservation and job tracker are what a unit test
/// cannot drive.
/// </summary>
internal static class ControlPreemptHolder
{
    private const string StartTool = "test/preempt_haul_start";
    private const string ReadTool = "test/preempt_haul_read";
    private const string TaskId = "deliver/stack";
    private const string Def = "ChunkSlate";
    private const int Count = 1;

    // The deliverer stands beside the chunk and drops it two cells away: a few hundred ticks at most.
    private const int Budget = 2400;

    private static readonly FixtureOp<(string Holder, string Stack), Fx.PreemptHaulRead> Start = new(
        StartTool, a => ServedOps.Args(("holder", a.Holder), ("stack", a.Stack)), ServedOps.Reply<Fx.PreemptHaulRead>(StartTool));

    private static readonly FixtureOp<(string Holder, string Stack), Fx.PreemptHaulRead> Read = new(
        ReadTool, a => ServedOps.Args(("holder", a.Holder), ("stack", a.Stack)), ServedOps.Reply<Fx.PreemptHaulRead>(ReadTool));

    // A 9x3 clear site: the holder at the left edge, six cells from the chunk; the deliverer beside the chunk; the holder's
    // haul destination a one-cell stockpile at the right edge; the delivery destination a plain cell left of the chunk.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 9,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 3), Kinds = [Fx.StageClearKind.Things] }],
        Things = [new Fx.StageThing { Name = "stack", Def = Def, Count = Count, Cell = new(6, 1) }],
        Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(8, 1)], Priority = Fx.StageStoragePriority.Important, Allow = [Def] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "holder", Colonist = 0, Cell = new(0, 1), AllWork = 0,
                Work = new Dictionary<string, int> { ["Hauling"] = 1 }, Timetable = Fx.StageTimeAssignment.Work,
            },
            new Fx.StagePawn { Name = "deliverer", Colonist = 1, Cell = new(6, 0), AllWork = 0, Timetable = Fx.StageTimeAssignment.Work },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/coupled-preempts-vanilla-holder",
        Scope = "Issue #3671: an unowned colonist (Hauling on) runs a vanilla haul that reserves a slate chunk; a Hands Deliver "
            + "step for a second colonist on the same chunk is admitted, ends the holder's haul and completes, with no "
            + "target_unreachable_or_reserved fault. The fixture starts the holder's vanilla haul while paused instead of "
            + "waiting for its think tree to pick it. A unit test cannot drive the native reservations and job tracker.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 2 },
        Stage = Spec,
        Ops = [StartTool, ReadTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var holder = staged.Pawns["holder"].Id;
        var deliverer = staged.Pawns["deliverer"].Id;
        var stack = staged.Things["stack"].Ids.Single();
        var origin = staged.Things["stack"].Cells.Single();
        var site = staged.Site ?? throw new AcceptanceException("the preempt stage found no site");
        var destination = new Cell(site.X + 4, site.Z + 2);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);

        var started = await s.FixtureAsync("haul", Start, (holder, stack), cancellationToken).ConfigureAwait(false);
        s.RecordFixture("holder-started", started);
        if (!started.Holds)
        {
            throw new AcceptanceException($"the holder's vanilla haul does not reserve the chunk: {ServedOps.Json(started)}");
        }

        var step = new PawnActivityStep(PawnActivityKind.Deliver, stack, "", "", Count, int.MaxValue).At(destination);
        var batch = new PawnActivities(generation, [new PawnActivityOrder(deliverer, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, deliverer, [], step, new Cell(origin.X, origin.Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new RimGovernor.Brain.GameOrders.Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        RimGovernor.Brain.GameFacts.Operations.PawnActivityExecution? execution = null;
        _ = await s.RunUntilAsync("deliver", Budget, async ct =>
        {
            var tasks = await s.CallAsync("execution", NativeOps.ReadActivityTasks, new RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest(s.Identity, batch), ct).ConfigureAwait(false);
            execution = tasks.Pawns.FirstOrDefault(p => p.Pawn == deliverer)?.Execution;
            if (execution?.State == "failed")
            {
                throw new AcceptanceException($"the delivery faulted instead of preempting the holder: {execution}");
            }

            return ($"{execution?.State} {execution?.Reason}", execution?.State == "completed");
        }, cancellationToken).ConfigureAwait(false);
        s.Record("execution", execution?.ToString() ?? "none");
        if (execution?.State != "completed")
        {
            throw new AcceptanceException($"the delivery of {stack} by {deliverer} did not complete within {Budget} ticks: {execution?.ToString() ?? "no execution row"}");
        }

        var after = await s.FixtureAsync("holder", Read, (holder, stack), cancellationToken).ConfigureAwait(false);
        s.RecordFixture("holder-after", after);
        if (after.JobId == started.JobId)
        {
            throw new AcceptanceException($"the holder's vanilla haul {started.JobId} still runs after the coupled delivery completed: {ServedOps.Json(after)}");
        }
    }
}
