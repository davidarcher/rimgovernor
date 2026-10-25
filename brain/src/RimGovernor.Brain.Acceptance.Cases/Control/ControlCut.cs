using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-cut-designated</c> (#3613): three young oaks under CutPlant designations the brain stood (as the
/// room reconcile's Cut wave does). The construction activity read reports them as one Cut removal op naming them; one
/// Hands Work/CutPlant set order over the three cuts them all under voluntary control, which otherwise gives the
/// designations no worker. <c>control/pawn-activity-cut-removed</c>: the same order with one tree removed once the order stands; the
/// step completes on the remaining two instead of faulting. The native job tracker is what a unit test cannot drive.
/// </summary>
internal static class ControlCut
{
    private const string TaskId = "cut/wave";
    private const string Tree = "Plant_TreeOak";
    private const int Budget = 7500;

    // A 5x3 clear site beside the colonist with three young oaks (not yet harvestable, so a CutPlant stays a CutPlant) in a row.
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
        Plants = [new Fx.StagePlant { Name = "trees", Def = Tree, Cells = [new(1, 1), new(2, 1), new(3, 1)], Growth = 0.3f }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case(bool removed = false) => new()
    {
        Name = removed ? "control/pawn-activity-cut-removed" : "control/pawn-activity-cut-designated",
        Scope = (removed
            ? "A Hands Work/CutPlant set order over three young oaks the brain designated for cutting, with the tree farthest from "
                + "the colonist removed once the order stands: the step skips the gone member and completes on the remaining two "
                + "(the execution ends completed, not failed) within the budget."
            : "Three young oaks the brain designated for cutting under voluntary control, which otherwise gives the designations no "
                + "worker: the construction activity read reports one Cut removal op naming the three (Targets, Id the lowest), "
                + "operations_apply admits one Hands Work/CutPlant set order over them, and the controlled colonist cuts all three "
                + "within the budget.")
            + " A unit test cannot drive the native job tracker.",
        Ticks = Budget,
        Ops = removed ? [FixtureStage.Tool] : [],
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        QuietWorld = true,
        Run = (s, ct) => RunAsync(s, removed, ct),
    };

    private static async Task RunAsync(TypedSession s, bool removed, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var worker = staged.Pawns["worker"].Id;
        var workerCell = staged.Pawns["worker"].Cell;
        var trees = staged.Plants["trees"].Ids.ToList();
        var cells = staged.Plants["trees"].Cells.ToList();
        if (trees.Count != 3 || cells.Count != 3)
        {
            throw new AcceptanceException($"stage: want three oaks, got ids [{string.Join(",", trees)}] at [{string.Join(",", cells)}]");
        }

        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        foreach (var tree in trees)
        {
            _ = await TypedApply.AppliedAsync(s, "designate", new RimGovernor.Brain.GameOrders.Operations.DesignateIntent
            { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(),
                Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant,
                Target = new() { Id = tree }, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard }, cancellationToken).ConfigureAwait(false);
        }

        // The standing designations are one Cut wave: the op named by the lowest plant, Targets the three, a row for the colonist.
        var read = await s.CallAsync("removals", NativeOps.ReadConstructionActivity, new RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest(s.Identity, default), cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        var ops = read.Removals.Where(r => r.Kind == RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.Cut).ToList();
        if (ops is not [var op] || op.Targets.Count != 3 || !trees.All(op.Targets.Contains) || op.Id != op.Targets[0])
        {
            throw new AcceptanceException($"removals: want one Cut op naming the three oaks [{string.Join(",", trees)}], got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }

        var row = op.Options.FirstOrDefault(r => r.Pawn == worker);
        if (row is null || row.WorkGiver.Length == 0 || row.Finish is not RimGovernor.Brain.GameFacts.Observations.ConstructionFinish.Finishes { Ticks: > 0 })
        {
            throw new AcceptanceException($"removals: want a row for {worker} with a work giver, a known route and finish, got [{string.Join(";", op.Options)}]");
        }

        var step = new PawnActivityStep(PawnActivityKind.Work, op.Id, row.WorkGiver, "CutPlant", op.Targets.Count, int.MaxValue) { Targets = op.Targets };
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, row.Location, 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new RimGovernor.Brain.GameOrders.Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        var rect = new Fx.StageRect(site.X, site.Z, 5, 3);
        async Task<List<Fx.StageCell>> StandingAsync(string label, CancellationToken ct)
        {
            var grid = await TypedApply.CellsAsync(s, label, rect, ct).ConfigureAwait(false);
            return cells.Where(c => grid.ThingsAt(new Cell(c.X, c.Z)).Any(t => t.Def == Tree)).ToList();
        }

        var standing = cells;
        var cut = 0;
        if (removed)
        {
            // The order stands (its designations placed, the game still paused) when the member farthest from the colonist
            // goes: a run's shortest wait is hundreds of ticks here, past a young oak's cutting, so the removal is made
            // before the clock moves and the set completes on the two it can still reach.
            standing = await StandingAsync("ordered", cancellationToken).ConfigureAwait(false);
            if (standing.Count != 3)
            {
                throw new AcceptanceException($"an oak fell before the clock moved: standing [{string.Join(",", standing)}]");
            }

            var far = standing.OrderByDescending(c => (c.X - workerCell.X) * (c.X - workerCell.X) + (c.Z - workerCell.Z) * (c.Z - workerCell.Z)).ThenBy(c => c.X).First();
            _ = await s.StageAsync("remove", new Fx.StageSpec { Paused = true, Clear = [new Fx.StageClear { Rect = new Fx.StageRect(far.X, far.Z, 1, 1), Kinds = [Fx.StageClearKind.Things] }] }, cancellationToken).ConfigureAwait(false);
            s.Record("removed", $"{far.X},{far.Z}");
            cut = 1;
        }

        _ = await s.RunUntilAsync("cut", Budget, async ct =>
        {
            standing = await StandingAsync("cut", ct).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"standing={standing.Count}"), standing.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left oaks standing after {Budget} ticks: [{string.Join(",", standing)}]");
        }

        // The execution row: a set step ends completed when its count is done or no member is left (a removed tree is skipped, not a fault).
        var tasks = await s.CallAsync("execution", NativeOps.ReadActivityTasks, new RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest(s.Identity, batch), cancellationToken).ConfigureAwait(false);
        var execution = tasks.Pawns.FirstOrDefault(p => p.Pawn == worker)?.Execution;
        s.Record("execution", execution?.ToString() ?? "none");
        if (execution?.State != "completed" || removed && execution.Reason != "no_legal_member")
        {
            throw new AcceptanceException($"the set order over {3 - cut} standing oaks did not end completed{(removed ? " for want of a legal member" : "")}: {execution}");
        }

        if (!tasks.SatisfiedTasks.Contains(TaskId))
        {
            throw new AcceptanceException($"the continuation does not report the cleared set satisfied: bindings [{string.Join(";", tasks.Bindings)}], satisfied [{string.Join(",", tasks.SatisfiedTasks)}]");
        }
    }
}
