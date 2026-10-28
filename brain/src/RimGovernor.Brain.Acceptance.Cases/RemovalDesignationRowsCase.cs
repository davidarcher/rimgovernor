using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/removal-designation-rows</c> (#3557, #3612, #3614, #3615): the construction activity read's standing
/// removals as clearing ops: a Deconstruct op over a ruin the brain designated by an enclosure-guarded Designate (as the
/// room reconcile stands a FurnitureOut), an Uninstall op over the colony's own bed it designated for uninstall (as the
/// reconcile's Pack does), a Haul op over a slate chunk it designated for hauling (a native chunk designation),
/// carried to the stockpile beside it, and a RemoveFloor op over a floored cell it designated for floor removal (as the
/// reconcile's FloorOut does). The native designation manager, the pieces' removability, the removal jobs' work and the
/// storage scan are game mechanics a unit test cannot establish.
/// </summary>
internal static class RemovalDesignationRowsCase
{
    private const string Stuff = "WoodLog";
    private const string Ruin = "Door";
    private const string Piece = "Bed";
    private const string Stack = "ChunkSlate";
    private const string Floor = "WoodPlankFloor";
    private const int StackCount = 1;
    private static readonly Fx.StageCell Floored = new(2, 2);

    // A 5x3 clear site beside the colonist with an ancients' door at its middle cell, the colony's bed at its left edge,
    // a slate chunk at its right edge, a one-cell chunk stockpile above it and a wood-plank floor above the door.
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
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(Floored.X, Floored.Z, 1, 1), Terrain = Floor }],
        Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(4, 2)], Priority = Fx.StageStoragePriority.Important, Allow = [Stack] }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/removal-designation-rows",
        Scope = "An ancients' door under an enclosure-guarded Deconstruct Designate, the colony's bed under an Uninstall (relocate, "
            + "uninstall), a slate chunk (a designateHaulable thing) under a plain Haul Designate beside a chunk stockpile and a wood-plank "
            + "floor cell under a RemoveFloor: the construction activity read reports each "
            + "in Removals as a clearing op of its kind whose Id is the thing, one row per free colonist naming it (Site and Input its id, "
            + "Resource its def) with a route and a Finishes finish through the native deconstruct or uninstall work giver, or for the chunk "
            + "the stockpile cell as Location, its count as Count and a Carried delivery, or for the floor one RemoveFloor op "
            + "(Id removefloor/{x}/{z}/x1, Cells the cell) with a route and a Finishes finish through the remove-floors giver; an "
            + "undesignated thing has no op. Does not prove the brain's binding or the jobs (control/pawn-activity-deconstruct-designated, "
            + "control/pawn-activity-uninstall-designated, control/pawn-activity-haul-designated, control/pawn-activity-remove-floor-designated).",
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
        var floored = new Cell(site.X + Floored.X, site.Z + Floored.Z);
        var request = new RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest(s.Identity, default);

        var before = await s.CallAsync("undesignated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        if (before.Removals.Any(r => r.Id == ruin || r.Id == piece || r.Id == stack || r.Kind == RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.RemoveFloor))
        {
            throw new AcceptanceException($"undesignated: the read reports a removal op for {ruin}, {piece}, {stack} or a floor before any designation stands: [{string.Join(";", before.Removals)}]");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "designate", WallKit.Deconstruct(ruin), cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "uninstall", new RimGovernor.Brain.GameOrders.Operations.RelocateIntent { ThingId = piece, Mode = new RimGovernor.Brain.GameOrders.Operations.RelocationMode.Uninstall() }, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "haul", new RimGovernor.Brain.GameOrders.Operations.DesignateIntent { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul, Target = new() { Id = stack }, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard }, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "floor", new RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent { Cell = WallKit.Wire(floored), DefName = Floor }, cancellationToken).ConfigureAwait(false);
        var read = await s.CallAsync("designated", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        s.RecordWire("removals", read.Removals);
        Check(read, ruin, Ruin, staged.Buildings["ruin"].Cells.Single(), RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.Deconstruct);
        Check(read, piece, Piece, staged.Buildings["piece"].Cells.Single(), RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.Uninstall);
        CheckHaul(read, stack, staged.Things["stack"].Cells.Single(), new Cell(site.X + store.X, site.Z + store.Z));
        CheckFloor(read, floored);
    }

    // The floor cell's RemoveFloor op (#3615): a cell set op of one cell, rows in the removal shape routed to the cell.
    private static void CheckFloor(RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead read, Cell floored)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"removefloor/{floored.X}/{floored.Z}/x1");
        var ops = read.Removals.Where(r => r.Kind == RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.RemoveFloor).ToList();
        if (ops is not [var op] || op.Id != id || op.Cells.Count != 1 || op.Cells[0] != floored || op.Options.Count == 0 || op.Options.Any(r => r.Site != id
            || r.Definition != Floor || r.Location != floored || r.WorkGiver.Length == 0 || r.Finish is not RimGovernor.Brain.GameFacts.Observations.ConstructionFinish.Finishes { Ticks: > 0 }))
        {
            throw new AcceptanceException($"designated: want one RemoveFloor op {id} over {floored} ({Floor}) with a row per free colonist at the cell with a "
                + $"known route and finish, got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }

    // The stack's Haul op (#3614): the planned-clearing haul rows, carried to the stockpile cell.
    private static void CheckHaul(RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead read, string stack, Fx.StageCell origin, Cell store)
    {
        var ops = read.Removals.Where(r => r.Id == stack).ToList();
        if (ops is not [var op] || op.Kind != RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind.Haul || op.Options.Count == 0 || op.Options.Any(r => r.Site != stack || r.Input != stack
            || r.Resource != Stack || r.Definition != Stack || r.Origin != new Cell(origin.X, origin.Z) || r.Location != store || r.Count != StackCount || r.Capacity <= 0
            || r.WorkGiver.Length == 0 || r.Delivery is not RimGovernor.Brain.GameFacts.Observations.ConstructionDelivery.Carried { DeliveryTicks: > 0 }))
        {
            throw new AcceptanceException($"designated: want one Haul op for {stack} ({Stack}) from {origin} to {store} with a row per free colonist naming it with "
                + $"known routes and delivery, got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }

    private static void Check(RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead read, string building, string definition, Fx.StageCell cell, RimGovernor.Brain.GameFacts.Observations.PlannedClearingKind kind)
    {
        var ops = read.Removals.Where(r => r.Id == building).ToList();
        if (ops is not [var op] || op.Kind != kind || op.Options.Count == 0 || op.Options.Any(r => r.Site != building || r.Input != building
            || r.Resource != definition || r.Definition != definition || r.Location != new Cell(cell.X, cell.Z)
            || r.WorkGiver.Length == 0 || r.Finish is not RimGovernor.Brain.GameFacts.Observations.ConstructionFinish.Finishes { Ticks: > 0 }))
        {
            throw new AcceptanceException($"designated: want one {kind} op for {building} ({definition}) at {cell} with a row per free colonist naming it with a "
                + $"known route and finish, got [{string.Join(";", read.Removals)}]; findings {string.Join(",", read.Findings)}");
        }
    }
}
