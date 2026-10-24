using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A barracks the plan holds (#3654): a station group planned as one tiles to one room with a bed slot per bed, the
/// bedroom step owes it a bed per slot and counts its sleepers housed, its planned occupants are the sleepers the step
/// bunks there (#3658), and the role saves under its wire name.
/// </summary>
public sealed class PlannedBarracksTests
{
    private const string Bed = "Bed";
    private static readonly PawnId A = new("a");
    private static readonly PawnId B = new("b");
    private static readonly InteriorPieceDef BedShape = new(Bed, new Cell(1, 2), RoomRole.Bedroom, null);
    internal static readonly RoomFurniture Furniture = new() { Beds = [new FurnitureBed { Def = Bed, Slots = 1 }] };

    /// <summary>A humanlike bed as the catalog scores it: a lone free one a bedroom, several a barracks.</summary>
    internal static readonly StationCatalog Catalog = new()
    {
        PlayBuildings = [],
        Stations = new Dictionary<string, Station>
        {
            [Bed] = new()
            {
                Shape = BedShape,
                EmptyCountsForBarracks = true,
                Contributions =
                [
                    new() { Role = RoomRole.Bedroom, When = RoleCondition.BedFree, Weight = 100000 },
                    new() { Role = RoomRole.Barracks, When = RoleCondition.BedFree, Weight = 100100, Additive = true },
                    new() { Role = RoomRole.Hospital, When = RoleCondition.BedMedical, Weight = 100000 },
                    new() { Role = RoomRole.PrisonCell, When = RoleCondition.BedPrisoner, Weight = 100000 },
                ],
            },
        },
    };

    internal static RoomGroup BarracksGroup => new()
    {
        Id = 0,
        Role = RoomRole.Barracks,
        Planned = PlannedRole.Barracks,
        Stations = [new GroupStation("bed", Bed, StationPurpose.Sleep, 3)],
    };

    internal static MapSurvey OpenMap() => new()
    {
        Bounds = new Bounds(40, 30),
        Cells = [.. Enumerable.Range(0, 40).SelectMany(x => Enumerable.Range(0, 30).Select(z => new SurveyCell { Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm }))],
    };

    [Fact]
    public void AGroupOfThreeBedsPlannedAsABarracksTilesToOneBarracksWithEveryBedReachable()
    {
        var group = BarracksGroup;
        Assert.Equal((PlannedRole.Barracks, true), LayoutTileRules.PlannedRoleOfGroup(group));
        var survey = OpenMap();
        var tiled = LayoutTileRules.TileEmbedding(new TileInput
        {
            Groups = [group],
            Embedding = new Embedding(),
            Blocks = [new EmbeddedBlock { Group = 0, Interior = new Rectangle(15, 12, 5, 4) }],
            Catalog = Catalog,
            Zones = LayoutZoningRules.Zone(survey),
            Survey = survey,
            Light = new LightPrice(),
            Graph = new StationGraph(),
            Furniture = Furniture,
            Dining = new DiningFurniture(),
            NextRoomId = 1,
        });

        var room = Assert.Single(tiled.Plan.Rooms);
        Assert.Equal(PlannedRole.Barracks, room.Role);
        var beds = tiled.Plan.Pieces[room.Id].Where(p => p.Def == Bed).ToList();
        Assert.Equal(3, beds.Count);
        Assert.Equal(3, SleepingRules.BedSlotsOf(tiled.Plan, room, Furniture)!.Count);

        // The game scores the three free beds a barracks.
        var pieces = beds.Select(p => new RoomPiece { Def = p.Def, State = RoleCondition.BedFree }).ToList();
        Assert.Equal(RoomRole.Barracks, RoomRoleRules.PredictRole(Catalog, pieces).Role);

        // Every bed has a free floor cell beside it that a pawn walks to from the room's door.
        var taken = tiled.Plan.Pieces[room.Id].SelectMany(p => PlannedRules.RectCells(p.Rect)).ToHashSet();
        var floor = room.Region.Cells().Where(c => !taken.Contains(c)).ToHashSet();
        var reached = new HashSet<Cell>();
        var queue = new Queue<Cell>(Neighbours(room.Door).Where(floor.Contains));
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!reached.Add(c))
            {
                continue;
            }
            foreach (var n in Neighbours(c).Where(n => floor.Contains(n) && !reached.Contains(n)))
            {
                queue.Enqueue(n);
            }
        }
        Assert.All(beds, b => Assert.Contains(PlannedRules.RectCells(b.Rect).SelectMany(Neighbours), reached.Contains));
    }

    private static IEnumerable<Cell> Neighbours(Cell c) => [new(c.X + 1, c.Z), new(c.X - 1, c.Z), new(c.X, c.Z + 1), new(c.X, c.Z - 1)];

    // An unplanned hall the game scores a Barracks (its beds house no one) and a 5x4 planned barracks whose three tiled
    // bed slots stand along its north wall.
    private static readonly PlannedRoom Hall = new() { Id = 1, Region = RegionRules.RectRegion(new Rectangle(0, 0, 5, 5)) };

    private static readonly PlannedRoom Barracks = new()
    {
        Id = 2, Role = PlannedRole.Barracks, Region = RegionRules.RectRegion(new Rectangle(10, 0, 5, 4)), Door = new Cell(12, -1), DoorRot = Rotation.South,
    };

    private static InteriorPiece Slot(int x) => new() { Slot = "bed", Def = Bed, Size = BedShape.Size, Rot = Rotation.South, Rect = new Rectangle(x, 2, 1, 2) };

    private static readonly LayoutPlan Plan = new()
    {
        Rooms = [Barracks],
        NextRoomId = 3,
        Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [Barracks.Id] = [Slot(10), Slot(12), Slot(14)] },
    };

    private static (RoomState Room, IEnumerable<Cell> Cells) Standing(string id, PlannedRoom room, RoomRole role, params string[] beds) => (RoomFixtures.State(id, role, beds: beds), room.Region.Cells());

    private static SleepingBed SleepingBed(string id, string room, params PawnId[] owners) => new()
    {
        Id = id, Definition = new(Bed), Humanlike = true, Room = Known(room), Owners = [.. owners], AccessibleTo = [A, B],
    };

    private static SleepingPerson Person(PawnId id, string bed) => new() { Id = id, OwnedBed = Known(bed) };

    private static BedroomStep Step((RoomState Room, IEnumerable<Cell> Cells)[] rooms, SleepingObservation sleeping) =>
        SleepingRules.NextBedroomStep(Plan, RoomFixtures.Census(rooms) with { Shapes = new PieceShapes { Furniture = Furniture } }, sleeping, new Dictionary<string, RoomTarget>(), new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>());

    [Fact]
    public void AnEmptyPlannedBarracksIsOwedItsReconcile()
    {
        var sleeping = new SleepingObservation { Colonists = 2, People = [Person(A, "s1"), Person(B, "s2")], Beds = [SleepingBed("s1", "hall", A), SleepingBed("s2", "hall", B)] };

        var unbuilt = Step([Standing("hall", Hall, RoomRole.Barracks, "s1", "s2")], sleeping);
        Assert.Equal((BedroomStepKind.Reconcile, Barracks.Id, 2), (unbuilt.Kind, unbuilt.Room.Id, unbuilt.Unhoused));

        // Standing with one of its three beds, and that one taken: the barracks is still owed the other two.
        var one = sleeping with { Beds = [.. sleeping.Beds, SleepingBed("k1", "barracks", A)], People = [Person(A, "k1"), Person(B, "s2")] };
        var partial = Step([Standing("hall", Hall, RoomRole.Barracks, "s1", "s2"), Standing("barracks", Barracks, RoomRole.Bedroom, "k1")], one);
        Assert.Equal((BedroomStepKind.Reconcile, Barracks.Id, 1), (partial.Kind, partial.Room.Id, partial.Unhoused));
    }

    [Fact]
    public void APawnInABarracksBedIsHousedAndAnUnhousedOneMovesIntoAVacantBarracksBed()
    {
        var rooms = new[] { Standing("hall", Hall, RoomRole.Barracks, "s1", "s2"), Standing("barracks", Barracks, RoomRole.Barracks, "k1", "k2", "k3") };
        var sleeping = new SleepingObservation
        {
            Colonists = 2,
            People = [Person(A, "k1"), Person(B, "s2")],
            Beds = [SleepingBed("s1", "hall"), SleepingBed("s2", "hall", B), SleepingBed("k1", "barracks", A), SleepingBed("k2", "barracks"), SleepingBed("k3", "barracks")],
        };

        // Only the hall sleeper is unhoused, and with no bedroom bed vacant they take a barracks bed.
        var move = Step(rooms, sleeping);
        Assert.Equal((BedroomStepKind.Move, B, "k2", "s2", 1), (move.Kind, move.Pawn, move.Bed, move.PreviousBed, move.Unhoused));

        // Both in the barracks: nobody is unhoused and nothing is owed (the third slot stands vacant).
        var both = sleeping with { People = [Person(A, "k1"), Person(B, "k2")], Beds = [SleepingBed("s1", "hall"), SleepingBed("s2", "hall"), SleepingBed("k1", "barracks", A), SleepingBed("k2", "barracks", B), SleepingBed("k3", "barracks")] };
        var none = Step(rooms, both);
        Assert.Equal(0, none.Unhoused);
        Assert.NotEqual(BedroomStepKind.Reconcile, none.Kind);
        Assert.NotEqual(BedroomStepKind.Move, none.Kind);
    }

    /// <summary>#3655: the occupant walk follows the step's order, so a bedroom planned before the barracks gets the first
    /// unhoused colonist, and one planned after a barracks that seats everyone gets nobody; the barracks' occupants are the
    /// colonists it seats (#3658).</summary>
    [Fact]
    public void APlannedBedroomsOccupantIsTheUnhousedColonistTheStepWouldMoveIn()
    {
        var bedroom = new PlannedRoom { Id = 3, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(20, 0, 4, 4)) };
        var sleeping = new SleepingObservation { Colonists = 2, People = [Person(A, "s1"), Person(B, "s2")], Beds = [SleepingBed("s1", "hall", A), SleepingBed("s2", "hall", B)] };
        var rooms = RoomFixtures.Census(Standing("hall", Hall, RoomRole.Barracks, "s1", "s2")) with { Shapes = new PieceShapes { Furniture = Furniture } };

        var first = SleepingRules.PlannedBedroomOccupants(Plan with { Rooms = [bedroom, Barracks] }, rooms, sleeping, null);
        Assert.Equal(["a"], Assert.Single(first, r => r.Key == 3).Value.ToArray());
        Assert.Equal(["b"], Assert.Single(first, r => r.Key == Barracks.Id).Value.ToArray());
        var seated = Assert.Single(SleepingRules.PlannedBedroomOccupants(Plan with { Rooms = [Barracks, bedroom] }, rooms, sleeping, null));
        Assert.Equal((Barracks.Id, "a,b"), (seated.Key, string.Join(",", seated.Value)));
    }

    /// <summary>#3680: the split keeps the standing barracks with its spare bunk and one sleeper fewer, two slots and a
    /// bedroom for two colonists, so the owner past its one seat is owed the bedroom (id order without gains) and is its
    /// planned occupant.</summary>
    [Fact]
    public void AnOwnerPastTheBarracksSlotsIsOwedTheSplitBedroom()
    {
        var bedroom = new PlannedRoom { Id = 3, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(20, 0, 4, 4)) };
        var split = Plan with { Rooms = [Barracks, bedroom], Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [Barracks.Id] = [Slot(10), Slot(12)] } };
        var rooms = RoomFixtures.Census(Standing("barracks", Barracks, RoomRole.Barracks, "k1", "k2")) with { Shapes = new PieceShapes { Furniture = Furniture } };
        var sleeping = new SleepingObservation { Colonists = 2, People = [Person(A, "k1"), Person(B, "k2")], Beds = [SleepingBed("k1", "barracks", A), SleepingBed("k2", "barracks", B)] };

        var step = SleepingRules.NextBedroomStep(split, rooms, sleeping, new Dictionary<string, RoomTarget>(), new Dictionary<PawnId, TraitEffects>(), new Dictionary<PawnId, double>());
        Assert.Equal((BedroomStepKind.Reconcile, bedroom.Id, 1), (step.Kind, step.Room.Id, step.Unhoused));
        var occupant = Assert.Single(SleepingRules.PlannedBedroomOccupants(split, rooms, sleeping, null));
        Assert.Equal((bedroom.Id, "a"), (occupant.Key, string.Join(",", occupant.Value)));
    }

    [Fact]
    public void ABarracksReconcileListsABedAtEveryTiledBedSlot()
    {
        var shapes = new PieceShapes { Defs = new Dictionary<string, InteriorPieceDef> { [Bed] = BedShape }, Furniture = Furniture };
        var slots = SleepingRules.BedSlotsOf(Plan, Barracks, Furniture);
        Assert.NotNull(slots);
        Assert.Null(SleepingRules.BedSlotsOf(Plan, Hall, Furniture));

        var furnishing = SleepingRules.BedroomFurnishing(Barracks, shapes, Bed, [], null, slots);

        Assert.True(furnishing.Absent);
        Assert.Equal(slots!.Select(s => s.Wanted()), furnishing.Pieces);
    }

    [Fact]
    public void ABarracksRoomSavesAndLoadsUnderItsWireName()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Codecs.Default.Write(writer, Plan);
        }
        var json = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"Role\":\"barracks\"", json, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(json);
        var back = Codecs.Default.Read<LayoutPlan>(doc.RootElement);
        Assert.Equal([PlannedRole.Barracks], back.Rooms.Select(r => r.Role));
    }
}
