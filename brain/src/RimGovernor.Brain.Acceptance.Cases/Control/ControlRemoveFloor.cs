using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-remove-floor-designated</c> (#3615): four wood-plank floor cells under RemoveFloor designations the
/// brain stood (as the room reconcile's FloorOut wave does). The construction activity read reports them as one RemoveFloor
/// removal op naming the cells; one Hands Work/RemoveFloor cell set order over the four removes them all under voluntary
/// control, which otherwise gives the designations no worker. The native job tracker is what a unit test cannot drive.
/// </summary>
internal static class ControlRemoveFloor
{
    private const string TaskId = "removefloor/wave";
    private const string Floor = "WoodPlankFloor";
    private const int Budget = 7500;

    // A 5x3 clear site beside the colonist with a wood-plank floor on the four middle cells of its middle row.
    private static readonly Fx.StageRect Floored = new(1, 1, 4, 1);
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 5,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things] }],
        Cells = [new Fx.StageCells { Rect = Floored, Terrain = Floor }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-remove-floor-designated",
        Scope = "Four wood-plank floor cells the brain designated for floor removal under voluntary control, which otherwise gives the "
            + "designations no worker: the construction activity read reports one RemoveFloor removal op naming the four cells (Cells, Id "
            + "removefloor/{x}/{z}/x4 of the first), operations_apply admits one Hands Work/RemoveFloor cell set order over them, and the "
            + "controlled colonist removes all four floors within the budget (the execution ends completed, the continuation reports the "
            + "task satisfied). A unit test cannot drive the native job tracker.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var worker = staged.Pawns["worker"].Id;
        var rect = new Fx.StageRect(site.X, site.Z, 5, 3);
        var cells = WallKit.Cells(site, Floored);
        var start = await TypedApply.CellsAsync(s, "start", rect, cancellationToken).ConfigureAwait(false);
        if (cells.Count != 4 || WallKit.WithTerrain(start, cells, Floor) != 4)
        {
            throw new AcceptanceException($"stage: want four {Floor} cells at [{string.Join(",", cells)}], got {WallKit.WithTerrain(start, cells, Floor)}");
        }

        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < cells.Count; i++)
        {
            _ = await TypedApply.AppliedAsync(s, "floor-" + i.ToString(CultureInfo.InvariantCulture),
                new Operations.RemoveFloorIntent { Cell = WallKit.Wire(cells[i]), DefName = Floor }, cancellationToken).ConfigureAwait(false);
        }

        // The standing designations are one FloorOut wave: the op named by its first cell, Cells the four, a row for the colonist.
        var read = await s.CallAsync("removals", NativeOps.ReadConstructionActivity, new Observations.ConstructionActivityRequest(s.Identity, default), cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        var ops = read.Removals.Where(r => r.Kind == Observations.PlannedClearingKind.RemoveFloor).ToList();
        var first = cells.OrderBy(c => c.X).ThenBy(c => c.Z).First();
        var id = string.Create(CultureInfo.InvariantCulture, $"removefloor/{first.X}/{first.Z}/x4");
        if (ops is not [var op] || op.Id != id || op.Cells.Count != 4 || !cells.All(op.Cells.Contains))
        {
            throw new AcceptanceException($"removals: want one RemoveFloor op {id} naming the four cells [{string.Join(",", cells)}], got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }

        var row = op.Options.FirstOrDefault(r => r.Pawn == worker);
        if (row is null || row.WorkGiver.Length == 0 || row.Definition != Floor || !cells.Contains(row.Location) || !row.SiteApproachTicks.IsKnown || !row.FinishTicks.IsKnown || row.FinishTicks.Value <= 0)
        {
            throw new AcceptanceException($"removals: want a row for {worker} with a work giver, the floor's def, a member cell as Location, a known route and finish, got [{string.Join(";", op.Options)}]");
        }

        var step = new PawnActivityStep(PawnActivityKind.Work, op.Id, row.WorkGiver, "RemoveFloor", op.Cells.Count, int.MaxValue) { Cells = op.Cells };
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, row.Location, 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        var left = cells.Count;
        _ = await s.RunUntilAsync("removed", Budget, async ct =>
        {
            left = WallKit.WithTerrain(await TypedApply.CellsAsync(s, "removed", rect, ct).ConfigureAwait(false), cells, Floor);
            return (string.Create(CultureInfo.InvariantCulture, $"floored={left}"), left == 0);
        }, cancellationToken).ConfigureAwait(false);
        if (left != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left {left} of four {Floor} cells floored after {Budget} ticks");
        }

        // The execution row: a cell set step ends completed when its count is done; the continuation reports the cleared set satisfied.
        var tasks = await s.CallAsync("execution", NativeOps.ReadActivityTasks, new Observations.ActivityTaskRequest(s.Identity, batch), cancellationToken).ConfigureAwait(false);
        var execution = tasks.Pawns.FirstOrDefault(p => p.Pawn == worker)?.Execution;
        s.Record("execution", execution?.ToString() ?? "none");
        if (execution?.State != "completed")
        {
            throw new AcceptanceException($"the cell set order over four floored cells did not end completed: {execution}");
        }

        if (!tasks.SatisfiedTasks.Contains(TaskId))
        {
            throw new AcceptanceException($"the continuation does not report the cleared set satisfied: bindings [{string.Join(";", tasks.Bindings)}], satisfied [{string.Join(",", tasks.SatisfiedTasks)}]");
        }
    }
}
