using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-deconstruct</c> (#3557): a Hands Work/Deconstruct step on an undesignated ruin. The order stands
/// the designation itself and the controlled colonist takes the native Deconstruct job; the native job tracker is what a
/// unit test cannot drive.
/// </summary>
internal static class ControlDeconstruct
{
    private const string TaskId = "deconstruct/ruin";
    private const string Giver = "Deconstruct";
    private const int Budget = 6000;

    // A 5x3 clear site beside the colonist with an ancients' door at its middle cell.
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
        Buildings = [new Fx.StageBuilding { Name = "ruin", Def = "Door", Stuff = "WoodLog", Cells = [new(2, 1)], Faction = Fx.StageFaction.Ancients }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-deconstruct",
        Scope = "A Hands Work/Deconstruct step on an undesignated ancients' door under voluntary control, which otherwise gives a "
            + "designation no worker: operations_apply admits the order, the order stands the deconstruct designation itself, and the "
            + "controlled colonist removes the door within the budget. A unit test cannot drive the native job tracker.",
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
        var worker = staged.Pawns["worker"].Id;
        var ruin = staged.Buildings["ruin"].Ids.Single();
        var cell = staged.Buildings["ruin"].Cells.Single();
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);

        var step = new PawnActivityStep(PawnActivityKind.Work, ruin, Giver, "Deconstruct", 0, int.MaxValue);
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, new Cell(cell.X, cell.Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Observations.BuildingState> standing = [];
        _ = await s.RunUntilAsync(
            "deconstruct",
            Budget,
            async ct =>
            {
                standing = await s.BuildingsAsync("ruin", new Observations.ListBuildingsRequest { Ids = [ruin], AnyFaction = true }, ct).ConfigureAwait(false);
                return (string.Create(CultureInfo.InvariantCulture, $"standing={standing.Count}"), standing.Count == 0);
            },
            cancellationToken).ConfigureAwait(false);
        s.Record("ruin", ruin);
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left the door {ruin} standing after {Budget} ticks: {standing[0]}");
        }
    }
}
