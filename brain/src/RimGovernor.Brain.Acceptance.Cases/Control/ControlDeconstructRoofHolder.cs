using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-deconstruct-roof-holder</c> (#3617): a lone ancients' wall holding a short strip of thin rock
/// roof that would collapse (leaving collapsed rocks) when the wall comes down. The construction activity read reports the
/// brain's Deconstruct designation on it as a valued op carrying the strip in Cells; one Hands order runs Work/RemoveRoof
/// as a cell set step over the strip, then Work/Deconstruct on the wall. The game's collapse rules, the NoRoof area and the
/// native job tracker are what a unit test cannot drive.
/// </summary>
internal static class ControlDeconstructRoofHolder
{
    private const string TaskId = "deconstruct/roof-holder";
    private const string Roof = "RoofRockThin";
    private const string Rubble = "CollapsedRocks";
    private const int Side = 9;
    private const int Budget = 7500;

    // A 9x9 clear, unroofed site beside the colonist: an ancients' wall at its centre under a thin rock roof over the wall
    // and the two cells east of it (every cell the wall alone holds up).
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = Side,
            Height = Side,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, Side, Side), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Roofs] }],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(4, 4, 3, 1), Roof = Roof }],
        Buildings = [new Fx.StageBuilding { Name = "wall", Def = "Wall", Stuff = "WoodLog", Cells = [new(4, 4)], Faction = Fx.StageFaction.Ancients }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-deconstruct-roof-holder",
        Scope = "A lone ancients' wall holding a three-cell thin rock roof, designated for deconstruction by the brain under voluntary "
            + "control: the construction activity read reports a valued Deconstruct op carrying the three roof cells, operations_apply "
            + "admits one Hands order of a Work/RemoveRoof cell set step over them then a Work/Deconstruct on the wall, and the "
            + "controlled colonist takes the roof off and the wall down within the budget with no collapse (no collapsed rocks). "
            + "A unit test cannot drive the game's collapse rules or the native job tracker.",
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
        var wall = staged.Buildings["wall"].Ids.Single();
        var wallCell = staged.Buildings["wall"].Cells.Single();
        var roof = Enumerable.Range(0, 3).Select(i => new Cell(wallCell.X + i, wallCell.Z)).ToList();
        var rect = new Fx.StageRect(site.X, site.Z, Side, Side);
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", WallKit.Deconstruct(wall), cancellationToken).ConfigureAwait(false);

        // The standing designation is a valued op: the wall's Deconstruct carrying the strip, a row for the colonist.
        var read = await s.CallAsync("removals", NativeOps.ReadConstructionActivity, new Observations.ConstructionActivityRequest(s.Identity, default), cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        var ops = read.Removals.Where(r => r.Kind == Observations.PlannedClearingKind.Deconstruct && r.Id == wall).ToList();
        if (ops is not [var op] || op.Cells.Count != roof.Count || !roof.All(op.Cells.Contains))
        {
            throw new AcceptanceException($"removals: want one Deconstruct op {wall} carrying the roof cells [{string.Join(",", roof)}], got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }

        var row = op.Options.FirstOrDefault(r => r.Pawn == worker);
        if (row is null || row.WorkGiver.Length == 0 || row.Finish is not Observations.ConstructionFinish.Finishes { Ticks: > 0 } || !roof.Contains(row.Location))
        {
            throw new AcceptanceException($"removals: want a row for {worker} with a work giver, a known route and finish, located at a roof cell, got [{string.Join(";", op.Options)}]");
        }

        var removeRoof = new PawnActivityStep(PawnActivityKind.Work, wall, "RemoveRoofs", "RemoveRoof", op.Cells.Count, int.MaxValue) { Cells = op.Cells };
        var deconstruct = new PawnActivityStep(PawnActivityKind.Work, wall, row.WorkGiver, "Deconstruct", 0, int.MaxValue);
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [removeRoof, deconstruct]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], deconstruct, row.Location, 0) { PriorSteps = [removeRoof] }],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Observations.BuildingState> standing = [];
        _ = await s.RunUntilAsync(
            "deconstruct",
            Budget,
            async ct =>
            {
                standing = await s.BuildingsAsync("wall", new Observations.ListBuildingsRequest { Ids = [wall], AnyFaction = true }, ct).ConfigureAwait(false);
                return (string.Create(CultureInfo.InvariantCulture, $"standing={standing.Count}"), standing.Count == 0);
            },
            cancellationToken).ConfigureAwait(false);
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left the wall {wall} standing after {Budget} ticks: {standing[0]}");
        }

        // The roof came off by the pawn's work, not by collapse: the strip is unroofed and no collapsed rocks lie on the site.
        var grid = await TypedApply.CellsAsync(s, "after", rect, cancellationToken).ConfigureAwait(false);
        var roofed = roof.Where(c => grid.At(c)?.Roofed != false).ToList();
        var rubble = grid.Where(c => grid.ThingsAt(c.Cell).Any(t => t.Def == Rubble)).ToList();
        s.Record("roofed", string.Join(",", roofed));
        s.Record("rubble", string.Join(",", rubble));
        if (roofed.Count != 0 || rubble.Count != 0)
        {
            throw new AcceptanceException($"the roof did not come off cleanly: still roofed [{string.Join(",", roofed)}], collapsed rocks at [{string.Join(",", rubble)}]");
        }

        var tasks = await s.CallAsync("execution", NativeOps.ReadActivityTasks, new Observations.ActivityTaskRequest(s.Identity, batch), cancellationToken).ConfigureAwait(false);
        var execution = tasks.Pawns.FirstOrDefault(p => p.Pawn == worker)?.Execution;
        s.Record("execution", execution?.ToString() ?? "none");
        if (execution?.State != "completed")
        {
            throw new AcceptanceException($"the roof-off-then-deconstruct order did not end completed: {execution}");
        }
    }
}
