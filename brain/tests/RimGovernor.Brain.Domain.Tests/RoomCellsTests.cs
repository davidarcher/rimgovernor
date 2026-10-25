using RimGovernor.Brain.Policy;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomCellsTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 };

    [Fact]
    public void EachCensusRoomGetsItsHeldGridCellsRowMajorAndOtherKeysAreNotRead()
    {
        // A 3x2 grid at (10, 20): key 1 on (0,0), (2,0) and (1,1); key 2 on (1,0) but fogged; key 3 on (0,1),
        // which no census room names; no grid cell carries the census room keyed 4.
        var grid = new FrameCellGrid(Grid(10, 20, 3, 2, [true, false, true, true, true, false], [1, 2, 1, 3, 1, null]));
        var rooms = new RoomsSnapshot
        {
            Context = Context,
            Rooms = [RoomFixtures.State("a", gridRoom: 1), RoomFixtures.State("fogged", gridRoom: 2), RoomFixtures.State("unkeyed", gridRoom: 4)],
        };
        var cells = GameClient.RoomCellsOf(rooms, grid);
        Assert.Equal(["a"], cells.Keys);
        Assert.Equal([new Cell(10, 20), new Cell(12, 20), new Cell(11, 21)], cells["a"]);
    }

    [Fact]
    public void AWindowCellNamesItsRoomOrNoRoomAndCarriesNoRoofOrZoneAsNone()
    {
        // A 3x1 grid: (0,0) in room 5, (1,0) in no room, (2,0) fogged.
        var source = Grid(0, 0, 3, 1, [true, true, false], [5, null, 5]);
        source.Roof[0] = "RoofConstructed";
        source.ZoneId[0] = "Zone_1";
        var (cells, fogged) = new FrameCellGrid(source).Window(new RimGovernor.Brain.Policy.Rectangle(0, 0, 3, 1));

        Assert.Equal(1UL, fogged);
        Assert.Equal(2, cells.Count);
        var (inRoom, outside) = (cells[0], cells[1]);
        Assert.Equal(new RimGovernor.Brain.Policy.CellRoom.InRoom(5), inRoom.Room);
        Assert.True(inRoom.Roofed);
        Assert.Equal("RoofConstructed", inRoom.Roof);
        Assert.True(inRoom.Zone);
        Assert.Equal("Zone_1", inRoom.ZoneId);
        Assert.IsType<RimGovernor.Brain.Policy.CellRoom.NoRoom>(outside.Room);
        Assert.False(outside.Roofed);
        Assert.Null(outside.Roof);
        Assert.False(outside.Zone);
        Assert.Null(outside.ZoneId);
        Assert.Empty(outside.Things);
    }

    [Fact]
    public void AClippedWindowRetainsThePublishedGridAndExactRowIndex()
    {
        var source = Grid(10, 20, 3, 2, [true, true, false, true, true, true], [null, null, null, null, 7, null]);
        var things = new RimGovernor.Brain.Policy.Thing[] { new() { Def = "Wall", Flags = RimGovernor.Brain.Policy.ThingMarks.Edifice } };
        source.Things[4] = things;
        source.Walkable[4] = true;
        source.Indoors[4] = true;
        source.Doorway[4] = true;
        source.Polluted[4] = true;
        source.FoundationRemoval[4] = true;
        source.StorageStacks[4] = 2;
        source.StorageCapacity[4] = 3;
        source.Fertility[4] = .75f;
        source.Glow[4] = .5f;
        source.Roof[4] = "RoofConstructed";
        source.ZoneId[4] = "Zone_1";
        source.Terrain[4] = "WoodPlankFloor";
        source.BaseTerrain[4] = "Soil";
        var frame = new FrameCellGrid(source);
        var rectangle = new RimGovernor.Brain.Policy.Rectangle(11, 20, 2, 2);
        var (cells, fogged) = frame.Window(rectangle);

        Assert.Equal(1UL, fogged);
        Assert.Equal([1, 4, 5], cells.Select(c => c.Index));
        Assert.All(cells, c => Assert.Same(source, c.Native));
        var row = cells[1];
        Assert.Equal(new Cell(11, 21), row.Cell);
        Assert.Same(things, row.Things);
        Assert.Equal(row, new RimGovernor.Brain.Policy.SiteCell(source, 4));
        Assert.NotEqual(row, new RimGovernor.Brain.Policy.SiteCell(source, 5));
        Assert.True(row.Walkable);
        Assert.True(row.Indoors);
        Assert.True(row.Doorway);
        Assert.True(row.Polluted);
        Assert.True(row.FoundationRemoval);
        Assert.Equal(2, row.StorageStacks);
        Assert.Equal(3, row.StorageCapacity);
        Assert.Equal(.75, row.Fertility);
        Assert.Equal(.5, row.Glow);
        Assert.True(row.Roofed);
        Assert.Equal(source.Roof[4], row.Roof);
        Assert.True(row.Zone);
        Assert.Equal(source.ZoneId[4], row.ZoneId);
        Assert.Equal(source.Terrain[4], row.Terrain);
        Assert.Equal(source.BaseTerrain[4], row.BaseTerrain);
        Assert.Equal(new RimGovernor.Brain.Policy.CellRoom.InRoom(7), row.Room);
        Assert.Same(cells, frame.Window(rectangle).Cells);
    }

    [Fact]
    public void FoggedCellsCannotBeExposedAsObservedRows()
    {
        var source = Grid(0, 0, 1, 1, [false], [7]);
        // Other columns have values, but Held alone says whether they represent facts.
        source.Roof[0] = "RoofConstructed";
        Assert.Throws<RequiredFactException>(() => new RimGovernor.Brain.Policy.SiteCell(source, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void CellIndicesMustLieInsideTheNativeGrid(int index)
    {
        var source = Grid(10, 20, 2, 1, [true, true], [null, null]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RimGovernor.Brain.Policy.SiteCell(source, index));
    }

    [Fact]
    public void AHeldArrayCannotAuthorizeAnIndexBeyondTheGridRectangle()
    {
        var source = Grid(0, 0, 1, 1, [true, true], [null]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RimGovernor.Brain.Policy.SiteCell(source, 1));
    }

    [Fact]
    public void GrowingRoomEligibilityDoesNotEraseTheObservedRoof()
    {
        var row = SiteCellFixtures.At(new(2, 3), roof: "RoofConstructed", indoors: true, room: 7);

        Assert.False(RimGovernor.Brain.Policy.CropChoiceRules.FreeCropSoil(row, .5, null).Ok);
        Assert.True(RimGovernor.Brain.Policy.CropChoiceRules.FreeCropSoilUnderRoof(row, .5, null).Ok);
        Assert.False(RimGovernor.Brain.Policy.CropChoiceRules.FreeCropSoilUnderRoof(row, .5, new HashSet<Cell> { row.Cell }).Ok);
        Assert.True(row.Roofed);
        Assert.Equal("RoofConstructed", row.Roof);
        Assert.Equal("RoofConstructed", row.Native.Roof[row.Index]);
        Assert.Equal(new RimGovernor.Brain.Policy.CellRoom.InRoom(7), row.Room);
    }
    // A grid of bare cells (unwalkable open ground, no roof, zone or things) carrying the given held and room arrays.
    private static RimGovernor.Brain.GameFacts.Mirror.CellGrid Grid(int x, int z, int width, int height, bool[] held, int?[] room)
    {
        var n = width * height;
        return new RimGovernor.Brain.GameFacts.Mirror.CellGrid
        {
            X = x, Z = z, Width = width, Height = height,
            Held = held, Room = room,
            Walkable = new bool[n], Indoors = new bool[n], Doorway = new bool[n], Polluted = new bool[n], InHome = new bool[n],
            TopLayerRemovable = new bool[n], FoundationRemoval = new bool[n],
            StorageStacks = new int[n], StorageCapacity = new int[n],
            Fertility = new float[n], Glow = new float[n], SnowDepth = new float[n],
            Terrain = [.. Enumerable.Repeat("Soil", n)], BaseTerrain = [.. Enumerable.Repeat("Soil", n)],
            Roof = new string?[n], ZoneId = new string?[n], Things = new RimGovernor.Brain.Policy.Thing[]?[n],
        };
    }

    private static readonly RoomsSnapshot Census = new()
    {
        Context = Context,
        Rooms =
        [
            RoomFixtures.State(
                "room-1",
                RoomRole.Bedroom,
                beds: ["bed-1"],
                doors: [new RimGovernor.Brain.GameFacts.Observations.RoomDoor { Cell = new(4, 2), Outside = new(4, 1), Outdoors = true, PlayerOwned = true, Open = false, HoldOpen = false, BlockedOpen = false, Forbidden = true }],
                contents: [new RimGovernor.Brain.GameFacts.Observations.RoomContent { DefName = "Bed", Units = 1 }],
                stats: [new RimGovernor.Brain.GameFacts.Observations.RoomStat { DefName = "Impressiveness", Value = 32.5 }],
                cellCount: 12,
                gridRoom: 7) with { Extents = new() { Minimum = new(2, 2), Maximum = new(5, 4) }, Center = new(3, 3) },
            RoomFixtures.State("room-2", RoomRole.Unrecognized, enclosed: false, roofed: false),
        ],
    };

    [Fact]
    public void TheRoomCensusRoundTripsExactly()
    {
        var json = OrdersJson.Serialize(Census);
        var back = OrdersJson.Deserialize<RoomsSnapshot>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(Census.Rooms, back.Rooms);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("temperatureC")]
    [InlineData("gridRoom")]
    [InlineData("doors")]
    public void ARoomMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(OrdersJson.Serialize(Census))!.AsObject();
        Assert.True(node["rooms"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RoomsSnapshot>(node.ToJsonString()));
    }
}
