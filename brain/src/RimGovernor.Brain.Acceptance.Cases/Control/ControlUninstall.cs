using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/pawn-activity-uninstall-designated</c> (#3612): a Hands Work/Uninstall step on the colony's own bed the brain
/// designated for uninstall (relocate, uninstall), as the room reconcile's Pack stands it. Under voluntary control the
/// designation gets no worker by itself; the order marks its record Ordered and the controlled colonist packs the bed.
/// The native job tracker is what a unit test cannot drive.
/// </summary>
internal static class ControlUninstall
{
    private const string TaskId = "uninstall/piece";
    private const string Giver = "Uninstall";
    private const int Budget = 6000;

    // A 5x3 clear site beside the colonist with the colony's bed at its middle cell.
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
        Buildings = [new Fx.StageBuilding { Name = "piece", Def = "Bed", Stuff = "WoodLog", Cells = [new(2, 0)] }],
        Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0 }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/pawn-activity-uninstall-designated",
        Scope = "A Hands Work/Uninstall step on the colony's bed the brain designated for uninstall (relocate, uninstall) under voluntary "
            + "control, which otherwise gives the designation no worker: operations_apply admits the order, the order marks the "
            + "designation's record Ordered, and the controlled colonist packs the bed within the budget. A unit test cannot drive the "
            + "native job tracker.",
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
        var piece = staged.Buildings["piece"].Ids.Single();
        var cell = staged.Buildings["piece"].Cells.Single();
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "uninstall", new Operations.RelocateIntent { ThingId = piece, Mode = new Operations.RelocationMode.Uninstall() }, cancellationToken).ConfigureAwait(false);

        var step = new PawnActivityStep(PawnActivityKind.Work, piece, Giver, "Uninstall", 0, int.MaxValue);
        var batch = new PawnActivities(generation, [new PawnActivityOrder(worker, TaskId, TaskId, 0, 1, false, [step]) { Task = TaskId }])
        {
            Tasks = [new SelectedActivityTask(TaskId, worker, [], step, new Cell(cell.X, cell.Z), 0)],
        };
        _ = await TypedApply.AppliedAsync(s, "order", new Operations.PawnActivitiesIntent { Batch = batch }, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Observations.BuildingState> standing = [];
        _ = await s.RunUntilAsync(
            "uninstall",
            Budget,
            async ct =>
            {
                standing = await s.BuildingsAsync("piece", new Observations.ListBuildingsRequest { Ids = [piece], Statuses = [Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
                return (string.Create(CultureInfo.InvariantCulture, $"standing={standing.Count}"), standing.Count == 0);
            },
            cancellationToken).ConfigureAwait(false);
        s.Record("piece", piece);
        if (standing.Count != 0)
        {
            throw new AcceptanceException($"the ordered colonist {worker} left the bed {piece} installed after {Budget} ticks: {standing[0]}");
        }
    }
}
