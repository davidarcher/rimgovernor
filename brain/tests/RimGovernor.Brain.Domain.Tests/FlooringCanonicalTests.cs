using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FlooringCanonicalTests
{
    [Fact]
    public void FlooringRetainsNativeRoomsCellsAndAbsentPendingFloor()
    {
        var cell = new O.FloorCell { Cell = new(2, 3), Terrain = "Soil", Pending = null };
        var native = new O.FloorRoom { Room = "room", Role = RoomRole.Bedroom, Cells = [cell] };
        var room = new RimGovernor.Brain.Policy.FloorRoom { Observed = native, RequiredTags = ["FineFloor"] };
        var census = new FlooringObservation
        {
            Rooms = [room],
            Terrains = new Dictionary<string, FloorTerrain> { ["Soil"] = new() { Natural = true } },
        };

        census.Validate();
        Assert.Same(native, room.Observed);
        Assert.Same(cell, Assert.Single(room.Cells));
        Assert.Null(room.Cells[0].Pending);
        Assert.Equal(native, WireJson.Deserialize<O.FloorRoom>(WireJson.Serialize(native)));

        var ordered = cell with { Pending = "WoodPlankFloor" };
        var changed = census with { Rooms = [room with { Observed = native with { Cells = [ordered] } }] };
        changed.Validate();
        Assert.Equal("WoodPlankFloor", changed.Rooms[0].Cells[0].Pending);
        Assert.Null(census.Rooms[0].Cells[0].Pending);
        Assert.Equal(room.RequiredTags, changed.Rooms[0].RequiredTags);

        var invalid = census with { Rooms = [room with { Observed = native with { Cells = [cell with { Pending = "" }] } }] };
        Assert.Throws<DomainException>(() => invalid.Validate());
    }
}
