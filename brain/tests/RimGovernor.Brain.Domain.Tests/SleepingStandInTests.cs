using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The day-one floor (#3659): while a sleeping and an indoor place are not both standing for everyone, the missing
/// sleeping places stand in as sleeping spots in the first selected sleeping room (its free interior, then the cells next
/// to its ring). A spot houses no one, so its sleeper is still walked into a bedroom, and a vacant one is taken down once
/// the beds in enclosed rooms take everyone.
/// </summary>
public sealed class SleepingStandInTests
{
    private static readonly PawnId A = new("a");

    private static PlannedRoom Planned(int id, PlannedRole role, int x) =>
        new() { Id = id, Role = role, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = 0, Width = 3, Height = 3 }) };

    private static readonly PlannedRoom Workshop = Planned(1, PlannedRole.Workshop, 0);
    private static readonly PlannedRoom Bedroom = Planned(2, PlannedRole.Bedroom, 10);
    private static readonly PlannedRoom Barracks = Planned(3, PlannedRole.Barracks, 20);
    private static readonly LayoutPlan Plan = new() { Rooms = [Workshop, Bedroom, Barracks], NextRoomId = 4 };

    [Fact]
    public void TheShortfallIsOwedUntilBothFootholdPlacesStand()
    {
        var bare = new RoundsFacts { Colonists = Known(3L), BedCapacity = Known(1L), IndoorCapacity = Known(0L) };
        Assert.Equal((3L, 2), ShelterRules.StandInsOwed(bare));
        // Every colonist has a spot but no roof yet: nothing more is owed.
        Assert.Equal(0, ShelterRules.StandInsOwed(bare with { BedCapacity = Known(3L) }).Missing);
        Assert.Equal(0, ShelterRules.StandInsOwed(bare with { BedCapacity = Known(3L), IndoorCapacity = Known(3L) }).Missing);
        Assert.Equal(0, ShelterRules.StandInsOwed(bare with { BedCapacity = Unknown<long>() }).Missing);
        Assert.Equal("stand-in-sleeping-3-2", ShelterRules.StandInMethod(3, 2));
        // The stand-ins are the floor, Survive tier; bed ownership and bedrooms stay Sustain.
        Assert.Equal(ConstructionTier.Survive, ConstructionTierRules.PlannerTier(ShelterFamily.Sleeping.FilingKey, Phase.HousingShelter));
        Assert.Equal(ConstructionTier.Sustain, ConstructionTierRules.PlannerTier(ShelterFamily.Sleeping.FilingKey, Phase.HousingSleeping));
    }

    [Fact]
    public void TheRoomIsTheFirstSelectedSleepingRoomElseTheFirstPlannedOne()
    {
        var selected = new PlannedSelection { Sites = [new SelectedPlannedSite(Barracks.Id, OpKind.WallIn, "Wall", "WoodLog", [])] };
        Assert.Equal(Barracks.Id, ShelterRules.StandInRoom(Plan, selected, _ => false)!.Id);
        Assert.Equal(Bedroom.Id, ShelterRules.StandInRoom(Plan, new PlannedSelection(), _ => false)!.Id);
        Assert.Equal(Barracks.Id, ShelterRules.StandInRoom(Plan, new PlannedSelection(), r => r.Id == Barracks.Id)!.Id);
        Assert.Null(ShelterRules.StandInRoom(new LayoutPlan { Rooms = [Workshop] }, new PlannedSelection(), _ => true));
    }

    private static readonly Cell Spot = new(1, 2);

    [Fact]
    public void TheAnchorsAreTheFreeInteriorThenTheRingsNeighboursAndOnlyTheNeighboursWithNoTiledPieces()
    {
        var bed = new InteriorPiece { Def = "Bed", Rect = new Rectangle { X = 10, Z = 0, Width = 1, Height = 2 } };
        var tiled = Plan with { Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [Bedroom.Id] = [bed] } };
        var ring = Bedroom.WallRing().Cells().ToHashSet();

        var anchors = ShelterRules.StandInAnchors(tiled, Bedroom, Spot);
        // A 1x2 spot at South covers its anchor and the cell below: (11,1), (11,2), (12,1), (12,2) fit beside the bed.
        var inside = anchors.TakeWhile(a => Bedroom.Region.Contains(a.Anchor)).ToList();
        Assert.Equal([new Cell(11, 1), new Cell(11, 2), new Cell(12, 1), new Cell(12, 2)], inside.Select(a => a.Anchor).Order(Comparer<Cell>.Create((l, r) => (l.X, l.Z).CompareTo((r.X, r.Z)))));
        Assert.All(inside, a => Assert.All(a.Footprint, c => Assert.True(Bedroom.Region.Contains(c) && !(c.X == 10 && c.Z <= 1))));
        // The south row's anchors would spill onto the ring (and its door): never offered, inside or out.
        Assert.All(anchors, a => Assert.DoesNotContain(a.Footprint, ring.Contains));
        Assert.All(anchors.Skip(inside.Count), a => Assert.All(a.Footprint, c => Assert.False(Bedroom.Region.Contains(c))));

        var bare = ShelterRules.StandInAnchors(Plan, Bedroom, Spot);
        Assert.NotEmpty(bare);
        Assert.All(bare, a => Assert.All(a.Footprint, c => Assert.False(Bedroom.Region.Contains(c) || ring.Contains(c))));
        Assert.All(bare, a => Assert.Contains(a.Footprint, c => ring.Any(w => Math.Abs(w.X - c.X) + Math.Abs(w.Z - c.Z) == 1)));

        // A neighbour room's floor and ring are never offered.
        var crowded = new LayoutPlan { Rooms = [Bedroom, Planned(5, PlannedRole.Workshop, 14)] };
        var neighbour = crowded.Rooms[1];
        Assert.DoesNotContain(ShelterRules.StandInAnchors(crowded, Bedroom, Spot).SelectMany(a => a.Footprint), c => neighbour.Region.Contains(c) || neighbour.WallRing().Contains(c));
    }

    [Fact]
    public void ThePickedStandInsNeverShareACell()
    {
        // (11,1) and (11,2) both preview placeable alone but share (11,1); a pending spot holds (12,0)..(12,1).
        IReadOnlyList<Cell>[] footprints = [[new(11, 0), new(11, 1)], [new(11, 1), new(11, 2)], [new(12, 0), new(12, 1)], [new(12, 1), new(12, 2)], [new(13, 1), new(13, 2)]];
        var blocked = new HashSet<Cell> { new(12, 0) };
        Assert.Equal([0, 3], ShelterRules.PickStandIns(footprints, blocked, _ => true, 2));
        // An unplaceable candidate is skipped and claims nothing.
        Assert.Equal([1, 3, 4], ShelterRules.PickStandIns(footprints, blocked, i => i != 0, 3));
    }

    private static Room Standing(string id, PlannedRoom room, params string[] beds) => new()
    {
        Id = id, Role = Known(RoomRole.Bedroom), Enclosed = Known(true), Cells = [.. room.Region.Cells()], Beds = [.. beds],
    };

    private static SleepingBed Bed(string id, string def, Fact<string> room, params PawnId[] owners) => new()
    {
        Id = id, Definition = new(def), Humanlike = true, Room = room, Owners = [.. owners], AccessibleTo = [A],
    };

    private static BedroomStep Step(LayoutPlan plan, Room[] rooms, SleepingObservation sleeping) =>
        SleepingRules.NextBedroomStep(plan, new RoomObservation { Rooms = [.. rooms] }, sleeping, new Dictionary<string, RoomTarget>(), new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>());

    private static readonly LayoutPlan OneBedroom = new() { Rooms = [Bedroom], NextRoomId = 3 };

    [Fact]
    public void AStandInHousesNoOneSoItsSleeperStillGetsTheBedroom()
    {
        var sleeping = new SleepingObservation
        {
            Colonists = 1,
            People = [new SleepingPerson { Id = A, OwnedBed = Known("x") }],
            Beds = [Bed("x", SleepingRules.SleepingSpotDefinition, Known("bedroom"), A)],
        };

        // The spot stands inside the enclosed bedroom: the bedroom still owes its bed.
        var step = Step(OneBedroom, [Standing("bedroom", Bedroom, "x")], sleeping);
        Assert.Equal((BedroomStepKind.Reconcile, Bedroom.Id), (step.Kind, step.Room.Id));
    }

    [Fact]
    public void AVacantStandInIsClearedOnceTheIndoorBedsTakeEveryone()
    {
        var outdoors = Bed("x", SleepingRules.SleepingSpotDefinition, Unknown<string>());
        var sleeping = new SleepingObservation
        {
            Colonists = 1,
            People = [new SleepingPerson { Id = A, OwnedBed = Known("w1") }],
            Beds = [outdoors, Bed("w1", "Bed", Known("bedroom"), A)],
        };

        var clear = Step(OneBedroom, [Standing("bedroom", Bedroom, "w1")], sleeping);
        Assert.Equal((BedroomStepKind.Clear, "x"), (clear.Kind, clear.Bed));

        // With no enclosed bed yet the stand-in is the colonist's place to sleep, and stays.
        var early = sleeping with { People = [new SleepingPerson { Id = A, OwnedBed = Known("") }], Beds = [outdoors] };
        Assert.NotEqual(BedroomStepKind.Clear, Step(OneBedroom, [], early).Kind);
    }
}
