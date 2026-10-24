using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomCellsTests
{
    private static readonly C.ObservationContext Context = new() { Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 };

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
        var cells = FacadeNativeClient.RoomCellsOf(rooms, grid);
        Assert.Equal(["a"], cells.Keys);
        Assert.Equal([new Cell(10, 20), new Cell(12, 20), new Cell(11, 21)], cells["a"]);
    }

    // A grid of bare cells (unwalkable open ground, no roof, zone or things) carrying the given held and room arrays.
    private static Mirror.CellGrid Grid(int x, int z, int width, int height, bool[] held, int?[] room)
    {
        var n = width * height;
        return new Mirror.CellGrid
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
                doors: [new O.RoomDoor { Cell = new(4, 2), Outside = new(4, 1), Outdoors = true, PlayerOwned = true, Open = false, HoldOpen = false, BlockedOpen = false, Forbidden = true }],
                contents: [new O.RoomContent { DefName = "Bed", Units = 1 }],
                stats: [new O.RoomStat { DefName = "Impressiveness", Value = 32.5 }],
                cellCount: 12,
                gridRoom: 7) with { Extents = new() { Minimum = new(2, 2), Maximum = new(5, 4) }, Center = new(3, 3) },
            RoomFixtures.State("room-2", RoomRole.Unrecognized, enclosed: false, roofed: false),
        ],
    };

    [Fact]
    public void TheRoomCensusRoundTripsExactly()
    {
        var json = WireJson.Serialize(Census);
        var back = WireJson.Deserialize<RoomsSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(Census.Rooms, back.Rooms);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("temperatureC")]
    [InlineData("gridRoom")]
    [InlineData("doors")]
    public void ARoomMissingAMemberDoesNotDecode(string member)
    {
        var node = JsonNode.Parse(WireJson.Serialize(Census))!.AsObject();
        Assert.True(node["rooms"]![0]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<RoomsSnapshot>(node.ToJsonString()));
    }
}
