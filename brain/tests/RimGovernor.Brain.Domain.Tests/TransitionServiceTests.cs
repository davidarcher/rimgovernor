using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using ClearanceTarget = RimGovernor.Brain.Policy.ClearanceTarget;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 8.1 with #3487: an unlisted standing piece's service is read, not left unassessed. Its occupancy (a bed's owners, a
/// bench's active bill) and its room contribution (its Beauty and MarketValue priced through the room-quality valuation) decide
/// whether it stays on merit; a piece the read finds serving no one goes as the room's furnishing omits it. A minifiable piece
/// with an unfilled planned slot of its def relocates there when that beats building the slot new.
/// </summary>
public sealed class TransitionServiceTests
{
    private static readonly PlannedRoom Bedroom = Room(PlannedRole.Bedroom);

    private static readonly WantedPiece BedSlot = new() { DefName = "Bed", Minimum = new Cell(11, 11), Maximum = new Cell(11, 11), Size = new Cell(1, 1), Rot = Rotation.North };

    private static PlannedRoom Room(PlannedRole role) => new()
    {
        Id = 1, Role = role, Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(12, 9), DoorSide = new DoorSide.Doored(Rotation.South),
    };

    private static ClearanceTarget Row(string id, string def, Cell at, bool inUse = false) =>
        new() { EntityId = id, DefName = def, Minimum = at, Maximum = at, Player = true, Deconstructible = true, InUse = inUse };

    private static SleepingBed Bed(string id, string room, params PawnId[] owners) => new()
    {
        Native = SleepingNativeFixtures.Bed(id: id, room: room, owners: [.. owners]), Building = SleepingNativeFixtures.Building(id: id, definition: new Resource("Bed")),
        Humanlike = true, RestEffectiveness = 1,
    };

    // The transition facts with the service read: each owner loses 0.3 capability-days a day without the bed, and the room-quality
    // facts are the RG-HABITAT-001 fixture's (one owner a, dull below 30 Impressiveness).
    private static TransitionFacts Read(SleepingObservation sleeping, TransitionFacts facts, TransitionBuild? build = null) => facts with
    {
        Standing = ServicingChoiceRules.StandingServiceOf(sleeping, (_, _) => new ExpectedLoss.PerDay(0.3), new Lazy<RoomImprovementFacts>(() => RoomImprovementTests.Facts()), facts),
        BuildPrice = build is null ? null : _ => build,
    };

    private static Reconciliation Reconcile(PlannedRoom room, IReadOnlyList<WantedPiece> template, IReadOnlyList<ClearanceTarget> rows, TransitionFacts facts) =>
        ReconcileRules.Reconcile(new ReconcileInput
        {
            Rooms = RoomTestFacts.NoRooms, Plan = new LayoutPlan { Rooms = [room] }, Room = room, Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(room, rows, template, null, _ => false), Furniture = template, Transitions = facts,
        });

    private static IEnumerable<string> Removed(Reconciliation r) =>
        r.Owed.Where(o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut).SelectMany(o => o.Targets).Select(t => t.EntityId);

    [Fact]
    public void AnUnownedBedOnAPlannedKitchenFootprintIsStoredAndAnOwnedBedStays()
    {
        var kitchen = Room(PlannedRole.Kitchen);
        var stove = new WantedPiece { DefName = "ElectricStove", Minimum = new Cell(11, 11), Maximum = new Cell(13, 11), Size = new Cell(3, 1), Rot = Rotation.North };
        var rows = new[] { Row("spare", "Bed", new Cell(12, 11)), Row("owned", "Bed", new Cell(13, 13), inUse: true) };
        var sleeping = new SleepingObservation { Beds = [Bed("spare", ""), Bed("owned", "", RoomImprovementTests.A)] };
        var facts = Read(sleeping, TransitionFixtures.Packing(rows));

        // The unowned bed serves no one (read, not unassessed) and stands where the stove goes: it is stored.
        Assert.Equal(new TransitionService.Assessed(0, 0), facts.Standing!(rows[0]));
        var r = Reconcile(kitchen, [stove], rows, facts);
        Assert.Contains(r.Owed, o => o.Kind == OpKind.Pack && o.Targets.Any(t => t.EntityId == "spare"));
        // The owned bed's owner would lose its rest over the horizon: it stays on merit, off the stove's footprint.
        Assert.Equal(new TransitionService.Assessed(0.3, 0), facts.Standing(rows[1]));
        Assert.DoesNotContain("owned", Removed(r));
        Assert.Empty(r.Holds);
    }

    [Fact]
    public void OmissionAloneNeverRemovesAndAGatedRemovalIsANamedHoldWithNoOrder()
    {
        // Task 8.3: a piece the furnishing omits, off every planned footprint, is compared on merit and stays at zero service.
        var kitchen = Room(PlannedRole.Kitchen);
        var stove = new WantedPiece { DefName = "ElectricStove", Minimum = new Cell(11, 11), Maximum = new Cell(13, 11), Size = new Cell(3, 1), Rot = Rotation.North };
        var rows = new[] { Row("spare", "Bed", new Cell(12, 11)), Row("idle", "Stool", new Cell(13, 13)) };
        var facts = Read(new SleepingObservation { Beds = [Bed("spare", "")] }, TransitionFixtures.Packing(rows));
        var idle = ServicingChoiceRules.Choose(new("idle", "Stool", TransitionCause.StaleInterior) { Service = facts.Standing!(rows[1]) }, facts);
        Assert.Equal(ServicingChoiceKind.Retain, idle.Chosen?.Kind);
        Assert.Null(idle.Hold);
        var served = Reconcile(kitchen, [stove], rows, facts);
        Assert.DoesNotContain("idle", Removed(served));
        Assert.Contains("spare", Removed(served));

        // The bed on the stove's footprint would be stored, but what it provides is not observed served by a replacement: no
        // removal is ordered, the piece stays named with its dependency, and nothing binds into the plan.
        var gated = Reconcile(kitchen, [stove], rows, facts with { Unserved = (t, _) => t == "spare" ? "rest:a" : null });
        Assert.DoesNotContain("spare", Removed(gated));
        var hold = Assert.Single(gated.Holds);
        Assert.Equal("replacement not serving:rest:a", hold.Reason);
        var decision = Assert.Single(gated.Transitions, d => d.Target == "spare");
        Assert.Equal(ServicingChoiceKind.Store, decision.Chosen?.Kind);
        Assert.False(decision.Admitted);
        Assert.Null(decision.Planned);
        Assert.Equal("replacement not serving:rest:a", decision.DependencyState);
        Assert.Equal(ServicingChoiceRules.ReplacementServing, Assert.Single(served.Transitions, d => d.Target == "spare").DependencyState);
    }

    [Fact]
    public void ABeautifulSculptureInAnOwnedBedroomStaysAndAPlainOneTheFurnishingOmitsStaysOnMerit()
    {
        // A dull-stage room one point above the mediocre line; the sculpture's beauty is what holds it there.
        var sleeping = new SleepingObservation { Beds = [Bed("b", "r", RoomImprovementTests.A)], Rooms = [RoomImprovementTests.Room(31)] };
        var row = Row("art", "SculptureSmall", new Cell(13, 13));
        TransitionFacts Sculpture(double beauty) => Read(sleeping, TransitionFixtures.Facts(TransitionFixtures.Mechanics("art", "SculptureSmall", row.Minimum, room: "r", beauty: beauty, wealth: beauty * 10)));

        var beautiful = Sculpture(50);
        Assert.True(Assert.IsType<TransitionService.Assessed>(beautiful.Standing!(row)).PerDay > 0);
        Assert.DoesNotContain("art", Removed(Reconcile(Bedroom, [BedSlot], [row], beautiful)));

        // Without beauty its taking out leaves the room's stage, so it serves no one and the bedroom's list omits it: the
        // omission alone is no cause (task 8.3), so staying is compared on merit and, costing nothing, wins.
        var plain = Sculpture(0);
        Assert.Equal(new TransitionService.Assessed(0, 0), plain.Standing!(row));
        var decision = ServicingChoiceRules.Choose(new("art", "SculptureSmall", TransitionCause.StaleInterior) { Service = plain.Standing(row) }, plain);
        Assert.Equal(ServicingChoiceKind.Retain, decision.Chosen?.Kind);
        Assert.Null(decision.Hold);
        Assert.DoesNotContain("art", Removed(Reconcile(Bedroom, [BedSlot], [row], plain)));
    }

    [Fact]
    public void AStalePieceOnAListedPiecesSlotCannotStayAndItsReadServicePricesItsRemoval()
    {
        // A plain sculpture the bedroom's list omits stands on the end table's planned slot: staying is excluded by the planned
        // footprint, so Relocate, Store and Remove compete, and the read service (no one served) prices zero service loss.
        var sleeping = new SleepingObservation { Beds = [Bed("b", "r", RoomImprovementTests.A)], Rooms = [RoomImprovementTests.Room(31)] };
        var row = Row("art", "SculptureSmall", new Cell(13, 13));
        var endTable = new WantedPiece { DefName = "EndTable", Minimum = row.Minimum, Maximum = row.Minimum, Size = new Cell(1, 1), Rot = Rotation.North };
        var facts = Read(sleeping, TransitionFixtures.Facts(TransitionFixtures.Mechanics("art", "SculptureSmall", row.Minimum, room: "r", beauty: 0, wealth: 0)));
        Assert.Equal(new TransitionService.Assessed(0, 0), facts.Standing!(row));

        var r = Reconcile(Bedroom, [BedSlot, endTable], [row], facts);
        var decision = Assert.Single(r.Transitions, d => d.Target == "art");
        Assert.Equal(TransitionCause.StaleInterior, decision.Cause);
        Assert.Equal(new ServicingStanding.Excluded(ReconcileRules.FootprintPlanned), Assert.Single(decision.Alternatives, a => a.Kind == ServicingChoiceKind.Retain).Standing);
        Assert.True(decision.Admitted);
        Assert.Contains(decision.Chosen!.Kind, (ServicingChoiceKind[])[ServicingChoiceKind.Store, ServicingChoiceKind.Remove]);
        Assert.Equal(0, Assert.IsType<ServicingStanding.Priced>(decision.Chosen.Standing).Price.Service);
        Assert.Contains("art", Removed(r));
        Assert.Empty(r.Holds);
    }

    [Fact]
    public void AMinifiableBedRelocatesIntoAnOpenBedSlotRatherThanTheSlotBeingBuiltNew()
    {
        // An owned bed standing off the planned bedroom's open bed slot: kept on merit while nothing prices the slot's build.
        var rows = new[] { Row("old", "Bed", new Cell(13, 13), inUse: true) };
        var sleeping = new SleepingObservation { Beds = [Bed("old", "", RoomImprovementTests.A)] };
        var kept = Reconcile(Bedroom, [BedSlot], rows, Read(sleeping, TransitionFixtures.Packing(rows)));
        Assert.Empty(Removed(kept));
        Assert.Contains(kept.Owed, o => o.Kind == OpKind.Build && o.Pieces.Contains(BedSlot));

        // With the slot's build priced, reinstalling the bed there avoids building a new one: one Reinstall op pairs the
        // standing bed with its slot (task 8.4, the game's one Reinstall order), with no separate pack, install or build.
        // The haul is the game's walk, not a distance: unread naming haul_unprobed until the probe answers the leg the reconcile
        // names (the bed's worker from its footprint to the slot), then the probe's ticks.
        var unprobed = Read(sleeping, TransitionFixtures.Packing(rows), new TransitionBuild.Priced(5));
        Assert.Equal(new TransitionLeg.Unread(ServicingRules.HaulUnprobed), ReconcileRules.RelocationSlots(rows, [BedSlot], [], unprobed)["old"].Haul);
        ReconcileInput Input(TransitionFacts transitions) => new()
        {
            Rooms = RoomTestFacts.NoRooms, Plan = new LayoutPlan { Rooms = [Bedroom] }, Room = Bedroom, Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(Bedroom, rows, [BedSlot], null, _ => false), Furniture = [BedSlot], Transitions = transitions,
        };
        var leg = Assert.Single(ReconcileRules.UnprobedHauls(Input(unprobed)));
        Assert.Equal(new RimGovernor.Brain.GameOrders.Observations.HaulProbeLeg("old", TransitionFixtures.Builder.Pawn, new Cell(13, 13), BedSlot.Minimum), leg);
        var facts = unprobed with { Hauls = (id, to) => id == "old" && to == BedSlot.Minimum ? new TransitionLeg.Walked(70) : null };
        var destinations = ReconcileRules.RelocationSlots(rows, [BedSlot], [], facts);
        var destination = destinations["old"];
        Assert.Equal(new TransitionLeg.Walked(70), destination.Haul);
        Assert.Same(BedSlot, destination.Piece);
        // With the haul unread, the move is unassessed and nothing is reinstalled; answered, no leg is left to ask.
        Assert.DoesNotContain(Reconcile(Bedroom, [BedSlot], rows, unprobed).Owed, o => o.Kind == OpKind.Reinstall);
        Assert.Empty(ReconcileRules.UnprobedHauls(Input(facts)));
        var moved = Reconcile(Bedroom, [BedSlot], rows, facts);
        var reinstall = Assert.Single(moved.Owed, o => o.Kind == OpKind.Reinstall);
        Assert.Equal("old", Assert.Single(reinstall.Targets).EntityId);
        Assert.Same(BedSlot, Assert.Single(reinstall.Pieces));
        Assert.Empty(Removed(moved));
        Assert.DoesNotContain(moved.Owed, o => o.Kind is OpKind.Build or OpKind.Install);

        // A piece with no open slot of its def has no destination.
        Assert.Empty(ReconcileRules.RelocationSlots(rows, [BedSlot with { DefName = "DoubleBed" }], [], facts));
    }
}
