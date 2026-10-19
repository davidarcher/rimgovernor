using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/deconstruct-designation-rows</c> (#3557): the construction activity read's removal rows over a ruin the
/// brain designated by an enclosure-guarded Designate, as the room reconcile stands a FurnitureOut. The native designation
/// manager, the door's deconstructibility and the deconstruct job's work are game mechanics a unit test cannot establish.
/// </summary>
internal static class DeconstructDesignationRowsCase
{
    private const string Stuff = "WoodLog";
    private const string Ruin = "Door";

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
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 5, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Roofs] }],
        Buildings = [new Fx.StageBuilding { Name = "ruin", Def = Ruin, Stuff = Stuff, Cells = [new(2, 1)], Faction = Fx.StageFaction.Ancients }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/deconstruct-designation-rows",
        Scope = "An ancients' door under an enclosure-guarded Deconstruct Designate: the construction activity read reports it in Removals, "
            + "one row per free colonist naming the door (Site and Input its id, Resource its def) with a known route and FinishTicks through "
            + "the native deconstruct work giver; an undesignated door has no row. Does not prove the brain's binding or the job "
            + "(control/pawn-activity-deconstruct-designated).",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var ruin = staged.Buildings["ruin"].Ids.Single();
        var cell = staged.Buildings["ruin"].Cells.Single();
        var request = new Observations.ConstructionActivityRequest(s.Identity, default);

        var before = await s.CallAsync("undesignated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        if (before.Removals.Any(r => r.Site == ruin))
        {
            throw new AcceptanceException($"undesignated: the read reports a removal row for {ruin} before any designation stands: [{string.Join(";", before.Removals)}]");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", WallKit.Deconstruct(ruin), cancellationToken).ConfigureAwait(false);
        var read = await s.CallAsync("designated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        var rows = read.Removals.Where(r => r.Site == ruin).ToList();
        if (rows.Count == 0 || rows.Any(r => r.Input != ruin || r.Resource != Ruin || r.Definition != Ruin || r.Location != new Cell(cell.X, cell.Z)
            || r.WorkGiver.Length == 0 || !r.SiteApproachTicks.IsKnown || !r.FinishTicks.IsKnown || r.FinishTicks.Value <= 0))
        {
            throw new AcceptanceException($"designated: want a removal row per free colonist naming {ruin} ({Ruin}) at {cell} with a known route and finish, "
                + $"got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }
}
