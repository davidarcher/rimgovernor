using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Doors by role (#3745, D4 of #3738). Nightly run 39931421801 saw the plan's Links fall from 7 to 0: every room got an
/// exterior door candidate and KeepRoutes then dropped the internal link doors while every room stayed reached through
/// its own door to the outside, and a room the placer could not furnish was built bare. A private room now keeps its
/// link doors, only an outdoor-facing role gets a door to the outside, a link door is dropped only while its rooms stay
/// joined over the other link doors, and an unfit room is dropped from the tiling with a decision.
/// </summary>
public sealed class TileDoorsByRoleTests
{
    private const string Bed = "Bed";
    private static readonly RoomFurniture Furniture = new() { Beds = [new FurnitureBed { Def = Bed, Slots = 1 }] };

    private static readonly StationCatalog Catalog = new()
    {
        Stations = new Dictionary<string, Station>
        {
            [Bed] = new()
            {
                Shape = new InteriorPieceDef(Bed, new Cell(1, 2), RoomRole.Bedroom, null),
                Contributions = [new() { Role = RoomRole.Bedroom, When = RoleCondition.BedFree, Weight = 100000 }],
            },
        },
    };

    private static MapSurvey OpenMap() => new()
    {
        Bounds = new Bounds(40, 30),
        Cells = [.. Enumerable.Range(0, 40).SelectMany(x => Enumerable.Range(0, 30).Select(z => new SurveyCell { Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm }))],
    };

    private static RoomGroup Bedroom(int id, int beds, params int[] links) => new()
    {
        Id = id,
        Role = RoomRole.Bedroom,
        Planned = PlannedRole.Bedroom,
        Stations = [new GroupStation("bed", Bed, StationPurpose.Sleep, beds)],
        Links = [.. links.Select(l => new GroupLink { To = l, Weight = 1 })],
    };

    /// <summary>A kitchen: crossing it tracks filth into the cooking (Clean), so it is no free thoroughfare.</summary>
    private static RoomGroup Kitchen(int id, params int[] links) => new()
    {
        Id = id,
        Role = RoomRole.Kitchen,
        Planned = PlannedRole.Kitchen,
        Requires = [EnvRequirement.Clean],
        Links = [.. links.Select(l => new GroupLink { To = l, Weight = 1 })],
    };

    private static RoomGroup Plain(int id, PlannedRole role, params int[] links) => new()
    {
        Id = id,
        Role = RoomRole.None,
        Planned = role,
        Links = [.. links.Select(l => new GroupLink { To = l, Weight = 1 })],
    };

    private static TileInput Input(IReadOnlyList<RoomGroup> groups, IReadOnlyList<Rectangle> rects)
    {
        var survey = OpenMap();
        return new TileInput
        {
            Groups = groups,
            Embedding = new Embedding(),
            Blocks = [.. rects.Select((r, i) => new EmbeddedBlock { Group = i, Interior = r })],
            Catalog = Catalog,
            Zones = LayoutZoningRules.Zone(survey),
            Survey = survey,
            Light = new LightPrice(),
            Graph = new StationGraph(),
            Furniture = Furniture,
            Dining = new DiningFurniture(),
            NextRoomId = 1,
        };
    }

    private static PlannedRoom RoomOf(TileResult t, PlannedRole role, int nth = 0) => t.Plan.Rooms.Where(r => r.Role == role).ElementAt(nth);

    [Fact]
    public void BedroomsLinkedToAKitchenKeepTheirLinkDoorsAndOnlyTheStorageOpensOutside()
    {
        // west bedroom | kitchen | east bedroom, with the storage north of the kitchen.
        RoomGroup[] groups = [Kitchen(0, 1, 2, 3), Bedroom(1, 1, 0), Bedroom(2, 1, 0), Plain(3, PlannedRole.Storage, 0)];
        Rectangle[] rects = [new(10, 10, 4, 3), new(6, 10, 3, 3), new(15, 10, 3, 3), new(10, 14, 4, 3)];

        var tiled = LayoutTileRules.TileEmbedding(Input(groups, rects));

        var kitchen = RoomOf(tiled, PlannedRole.Kitchen);
        var storage = RoomOf(tiled, PlannedRole.Storage);
        foreach (var bedroom in tiled.Plan.Rooms.Where(r => r.Role == PlannedRole.Bedroom))
        {
            var links = tiled.Plan.LinkCells(bedroom.Id);
            Assert.Single(links);
            Assert.Contains(tiled.Plan.Links, l => l.A == Math.Min(bedroom.Id, kitchen.Id) && l.B == Math.Max(bedroom.Id, kitchen.Id));
            Assert.Equal(links[0], bedroom.Door); // its only door is the link door: no door to the outside
        }
        Assert.Equal(3, tiled.Plan.LinkCells(kitchen.Id).Count);
        Assert.Contains(kitchen.Door, tiled.Plan.LinkCells(kitchen.Id));
        Assert.DoesNotContain(storage.Door, tiled.Plan.LinkCells(storage.Id));
        Assert.Single(tiled.Plan.Entrances);
        Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileExteriorDoor && d.Group == 3);
        Assert.True(LayoutPlan.CheckRoutes(tiled.Plan).Ok);
    }

    [Fact]
    public void ALinkedGroupWithNoOutdoorRoleRoomOpensOutsideThroughItsKitchenNotItsBarracks()
    {
        // barracks | kitchen, linked to each other and to nothing else: neither role faces outdoors, so the group is
        // sealed until it gets its one exterior door, which goes on the room that is not slept in.
        var barracks = Bedroom(0, 2, 1) with { Role = RoomRole.Barracks, Planned = PlannedRole.Barracks };
        RoomGroup[] groups = [barracks, Kitchen(1, 0)];
        Rectangle[] rects = [new(6, 10, 5, 4), new(12, 10, 4, 4)];

        var tiled = LayoutTileRules.TileEmbedding(Input(groups, rects));

        var b = RoomOf(tiled, PlannedRole.Barracks);
        var k = RoomOf(tiled, PlannedRole.Kitchen);
        Assert.Single(tiled.Plan.Links);
        Assert.Equal(tiled.Plan.LinkCells(b.Id).Single(), b.Door); // the barracks' only door is its link door
        Assert.DoesNotContain(k.Door, tiled.Plan.LinkCells(k.Id));
        Assert.Single(tiled.Plan.Entrances);
        Assert.Equal(1, Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileExteriorDoor).Group);
        Assert.True(LayoutPlan.CheckRoutes(tiled.Plan).Ok);
    }

    [Fact]
    public void KeepRoutesNeverDropsALinkDoorWhoseRoomsTheOtherLinksDoNotJoinEvenWithEveryRoomOpeningOutside()
    {
        // bedroom | kitchen | dining | storage in a row: the bedroom's way to dinner crosses the kitchen.
        RoomGroup[] groups = [Bedroom(0, 1, 1), Kitchen(1, 0, 2), Plain(2, PlannedRole.Dining, 1, 3), Plain(3, PlannedRole.Storage, 2)];
        Rectangle[] rects = [new(6, 10, 3, 3), new(10, 10, 4, 3), new(15, 10, 4, 3), new(20, 10, 3, 3)];
        var t = new Tiler(Input(groups, rects));
        t.Run();
        var before = t.Links.Keys.ToHashSet();
        Assert.Equal(3, before.Count);

        // Every room is given a door to the outside on its south wall, as the old exterior choice did.
        t.Through.Clear();
        t.ExtDoor = [.. t.Rooms.Select(r => (Cell?)new Cell(r.Rect.X, r.Rect.Z - 1))];
        t.ExtOut = [.. t.Rooms.Select(r => new Cell(r.Rect.X, r.Rect.Z - 2))];
        var check = LayoutPlan.CheckRoutes(t.Build());
        Assert.NotNull(check.Thoroughfare); // the kitchen is crossed: KeepRoutes has a door to weigh

        t.KeepRoutes();

        Assert.Equal(before, t.Links.Keys.ToHashSet());
        for (var i = 0; i < t.Rooms.Count; i++)
        {
            Assert.NotEmpty(t.LinkCells(i));
        }
        Assert.True(t.Through[1]); // the kitchen is walked through instead
        Assert.DoesNotContain(t.Out.Decisions, d => d.Reason == LayoutTileRules.ReasonTileLinkDropped);
    }

    [Fact]
    public void ARoomThePlacerCannotFurnishIsDroppedWithADecisionAndStaysOwed()
    {
        // A 3x3 bedroom cannot hold five beds behind its door; the storage stands apart.
        RoomGroup[] groups = [Plain(0, PlannedRole.Storage), Bedroom(1, 5)];
        Rectangle[] rects = [new(10, 10, 4, 3), new(20, 10, 3, 3)];

        var tiled = LayoutTileRules.TileEmbedding(Input(groups, rects));

        Assert.DoesNotContain(tiled.Plan.Rooms, r => r.Role == PlannedRole.Bedroom);
        Assert.False(tiled.Rooms.ContainsKey(1));
        var d = Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileUnfit);
        Assert.Equal(1, d.Group);
        Assert.Equal(LayoutTileRules.TileDropped, d.Verdict);
        Assert.NotEmpty(d.Why);
        Assert.Contains(PlannedRole.Bedroom, tiled.Plan.Owed);
        Assert.Single(tiled.Plan.Rooms, r => r.Role == PlannedRole.Storage);
    }

    [Fact]
    public void AStandingRoomThePlacerCannotFurnishStaysPinnedWithItsRefusalNoted()
    {
        RoomGroup[] groups = [Plain(0, PlannedRole.Storage), Bedroom(1, 5)];
        var input = Input(groups, [new(10, 10, 4, 3), new(20, 10, 3, 3)]);
        input = input with { Blocks = [input.Blocks[0], input.Blocks[1] with { Built = 7 }], NextRoomId = 8 };

        var tiled = LayoutTileRules.TileEmbedding(input);

        Assert.Equal(7, Assert.Single(tiled.Plan.Rooms, r => r.Role == PlannedRole.Bedroom).Id);
        var d = Assert.Single(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileUnfit);
        Assert.Equal((1, LayoutTileRules.TileUnsatisfied), (d.Group, d.Verdict));
        Assert.NotEmpty(d.Why);
    }
}
