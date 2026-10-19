using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/removal-designation-rows</c> (#3557, #3612, #3614): the construction activity read's standing removals
/// as clearing ops: a Deconstruct op over a ruin the brain designated by an enclosure-guarded Designate (as the room
/// reconcile stands a FurnitureOut), an Uninstall op over the colony's own bed it designated for uninstall (as the
/// reconcile's Pack does) and a Haul op over a slate chunk it designated for hauling (as the reconcile's HaulOut does),
/// carried to the stockpile beside it. The native designation manager, the pieces' removability, the removal jobs' work
/// and the storage scan are game mechanics a unit test cannot establish.
/// </summary>
internal static class RemovalDesignationRowsCase
{
    private const string Stuff = "WoodLog";
    private const string Ruin = "Door";
    private const string Piece = "Bed";
    private const string Stack = "ChunkSlate";
    private const int StackCount = 1;

    // A 5x3 clear site beside the colonist with an ancients' door at its middle cell, the colony's bed at its left edge,
    // a slate chunk at its right edge and a one-cell chunk stockpile above it.
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
        Buildings =
        [
            new Fx.StageBuilding { Name = "ruin", Def = Ruin, Stuff = Stuff, Cells = [new(2, 1)], Faction = Fx.StageFaction.Ancients },
            new Fx.StageBuilding { Name = "piece", Def = Piece, Stuff = Stuff, Cells = [new(0, 0)] },
        ],
        Things = [new Fx.StageThing { Name = "stack", Def = Stack, Count = StackCount, Cell = new(4, 0) }],
        Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(4, 2)], Priority = Fx.StageStoragePriority.Important, Allow = [Stack] }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/removal-designation-rows",
        Scope = "An ancients' door under an enclosure-guarded Deconstruct Designate, the colony's bed under an Uninstall (relocate, "
            + "uninstall) and a slate chunk (a designateHaulable thing) under a plain Haul Designate beside a chunk stockpile: the construction activity read reports each "
            + "in Removals as a clearing op of its kind whose Id is the thing, one row per free colonist naming it (Site and Input its id, "
            + "Resource its def) with a known route and FinishTicks through the native deconstruct or uninstall work giver, or for the chunk "
            + "the stockpile cell as Location, its count as Count and a known DeliveryTicks; an undesignated thing has no op. Does not prove "
            + "the brain's binding or the jobs (control/pawn-activity-deconstruct-designated, control/pawn-activity-uninstall-designated, "
            + "control/pawn-activity-haul-designated).",
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
        var piece = staged.Buildings["piece"].Ids.Single();
        var stack = staged.Things["stack"].Ids.Single();
        var site = staged.Site ?? throw new AcceptanceException("the removal stage found no site");
        var store = Spec.Zones[0].Cells[0];
        var request = new Observations.ConstructionActivityRequest(s.Identity, default);

        var before = await s.CallAsync("undesignated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        if (before.Removals.Any(r => r.Id == ruin || r.Id == piece || r.Id == stack))
        {
            throw new AcceptanceException($"undesignated: the read reports a removal op for {ruin}, {piece} or {stack} before any designation stands: [{string.Join(";", before.Removals)}]");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", WallKit.Deconstruct(ruin), cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "uninstall", new Operations.RelocateIntent { ThingId = piece, Uninstall = true }, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "haul", new Operations.DesignateIntent { Designation = Operations.ThingDesignation.Haul, Target = new() { Id = stack } }, cancellationToken).ConfigureAwait(false);
        var read = await s.CallAsync("designated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        Check(read, ruin, Ruin, staged.Buildings["ruin"].Cells.Single(), Observations.PlannedClearingKind.Deconstruct);
        Check(read, piece, Piece, staged.Buildings["piece"].Cells.Single(), Observations.PlannedClearingKind.Uninstall);
        CheckHaul(read, stack, staged.Things["stack"].Cells.Single(), new Cell(site.X + store.X, site.Z + store.Z));
    }

    // The stack's Haul op (#3614): the planned-clearing haul rows, carried to the stockpile cell.
    private static void CheckHaul(Observations.ConstructionActivityRead read, string stack, Fx.StageCell origin, Cell store)
    {
        var ops = read.Removals.Where(r => r.Id == stack).ToList();
        if (ops is not [var op] || op.Kind != Observations.PlannedClearingKind.Haul || op.Options.Count == 0 || op.Options.Any(r => r.Site != stack || r.Input != stack
            || r.Resource != Stack || r.Definition != Stack || r.Origin != new Cell(origin.X, origin.Z) || r.Location != store || r.Count != StackCount || r.Capacity <= 0
            || r.WorkGiver.Length == 0 || !r.ApproachTicks.IsKnown || !r.SiteApproachTicks.IsKnown || !r.DeliveryTicks.IsKnown || r.DeliveryTicks.Value <= 0))
        {
            throw new AcceptanceException($"designated: want one Haul op for {stack} ({Stack}) from {origin} to {store} with a row per free colonist naming it with "
                + $"known routes and delivery, got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }

    private static void Check(Observations.ConstructionActivityRead read, string building, string definition, Fx.StageCell cell, Observations.PlannedClearingKind kind)
    {
        var ops = read.Removals.Where(r => r.Id == building).ToList();
        if (ops is not [var op] || op.Kind != kind || op.Options.Count == 0 || op.Options.Any(r => r.Site != building || r.Input != building
            || r.Resource != definition || r.Definition != definition || r.Location != new Cell(cell.X, cell.Z)
            || r.WorkGiver.Length == 0 || !r.SiteApproachTicks.IsKnown || !r.FinishTicks.IsKnown || r.FinishTicks.Value <= 0))
        {
            throw new AcceptanceException($"designated: want one {kind} op for {building} ({definition}) at {cell} with a row per free colonist naming it with a "
                + $"known route and finish, got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }
}
