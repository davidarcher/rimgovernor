using System.Globalization;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-mine</c> (#3616): two undesignated natural rock cells, the shape of a planned site's Mine clearing.
/// One Hands Work/Mine set order over the two stands the Mine designations itself (the miner job needs one) under the
/// mine_safety guard as Ordered records, and the controlled colonist mines both under voluntary control, which otherwise
/// gives the rock no worker. The native job tracker and the pick hook are what a unit test cannot drive.
/// </summary>
internal static class ControlMine
{
    private const string TaskId = "mine/rock";
    private const string Giver = "Mine";
    private const string Rock = "Sandstone";
    private const int Budget = 7500;

    // A 5x3 clear, unroofed site beside the colonist with two sandstone cells in its middle row, a free cell between them;
    // the colonist mines at skill 20 so the picks land inside the budget.
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
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Roofs] }],
        Buildings = [new Fx.StageBuilding { Name = "rock", Def = Rock, Cells = [new(1, 1), new(3, 1)], Faction = Fx.StageFaction.None }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0, Skills = new Dictionary<string, int> { ["Mining"] = 20 } }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-mine",
        Scope = "A Hands Work/Mine set order over two undesignated natural rock cells under voluntary control, which otherwise gives "
            + "the rock no worker: operations_apply admits the order, the order stands the Mine designations itself under the "
            + "mine_safety guard, and the controlled colonist mines both within the budget. A unit test cannot drive the native job tracker.",
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
        var rocks = staged.Buildings["rock"].Ids.ToList();
        var cells = staged.Buildings["rock"].Cells.ToList();
        if (rocks.Count != 2 || cells.Count != 2)
        {
            throw new AcceptanceException($"stage: want two rocks, got ids [{string.Join(",", rocks)}] at [{string.Join(",", cells)}]");
        }

        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        var step = new PawnActivityStep(PawnActivityKind.Work, rocks[0], Giver, "Mine", rocks.Count, int.MaxValue) { Targets = EquatableArray.From(rocks) };
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, new Cell(cells[0].X, cells[0].Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        var rect = new Fx.StageRect(site.X, site.Z, 5, 3);
        var standing = cells;
        _ = await s.RunUntilAsync("mine", Budget, async ct =>
        {
            var grid = await TypedApply.CellsAsync(s, "mine", rect, ct).ConfigureAwait(false);
            standing = cells.Where(c => grid.ThingsAt(new Cell(c.X, c.Z)).Any(t => t.Def == Rock)).ToList();
            return (string.Create(CultureInfo.InvariantCulture, $"standing={standing.Count}"), standing.Count == 0);
        }, cancellationToken).ConfigureAwait(false);
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left rock standing after {Budget} ticks: [{string.Join(",", standing)}]");
        }
    }
}
