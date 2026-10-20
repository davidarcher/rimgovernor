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

    [Fact]
    public void TheCellsAreTheFreeInteriorThenTheRingsNeighboursAndOnlyTheNeighboursWithNoTiledPieces()
    {
        var bed = new InteriorPiece { Def = "Bed", Rect = new Rectangle { X = 10, Z = 0, Width = 1, Height = 2 } };
        var tiled = Plan with { Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [Bedroom.Id] = [bed] } };

        var cells = ShelterRules.StandInCells(tiled, Bedroom);
        var interior = Bedroom.Region.Cells().Where(c => !(c.X == 10 && c.Z <= 1)).ToList();
        Assert.Equal(interior, cells.Take(interior.Count));
        Assert.DoesNotContain(new Cell(10, 0), cells);
        Assert.All(cells.Skip(interior.Count), c => Assert.False(Bedroom.Region.Contains(c)));

        var bare = ShelterRules.StandInCells(Plan, Bedroom);
        Assert.NotEmpty(bare);
        Assert.All(bare, c => Assert.False(Bedroom.Region.Contains(c)));
        // Next to the ring, never on it.
        Assert.All(bare, c => Assert.False(Bedroom.WallRing().Contains(c)));
        Assert.All(bare, c => Assert.Contains(Bedroom.WallRing().Cells(), w => Math.Abs(w.X - c.X) + Math.Abs(w.Z - c.Z) == 1));

        // A neighbour room's floor and ring are never offered.
        var crowded = new LayoutPlan { Rooms = [Bedroom, Planned(5, PlannedRole.Workshop, 14)] };
        var neighbour = crowded.Rooms[1];
        Assert.DoesNotContain(ShelterRules.StandInCells(crowded, Bedroom), c => neighbour.Region.Contains(c) || neighbour.WallRing().Contains(c));
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
