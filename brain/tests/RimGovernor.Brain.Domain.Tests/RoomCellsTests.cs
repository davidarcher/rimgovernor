using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomCellsTests
{
    [Fact]
    public void EachCensusRoomGetsItsHeldGridCellsRowMajorAndOtherKeysAreNotRead()
    {
        // A 3x2 grid at (10, 20): key 1 on (0,0), (2,0) and (1,1); key 2 on (1,0) but fogged; key 3 on (0,1),
        // which no census room names.
        var grid = new FrameCellGrid(new Mirror.CellGrid
        {
            X = 10, Z = 20, Width = 3, Height = 2,
            Held = [true, false, true, true, true, false],
            Room = [1, 2, 1, 3, 1, null],
        });
        var rooms = new RoomsSnapshot
        {
            Rooms = EquatableArray.From(new[]
            {
                new RoomState { Id = "a", GridRoom = 1 },
                new RoomState { Id = "fogged", GridRoom = 2 },
                new RoomState { Id = "unkeyed" },
            }),
        };
        var cells = FacadeNativeClient.RoomCellsOf(rooms, grid);
        Assert.Equal(["a"], cells.Keys);
        Assert.Equal([new Cell(10, 20), new Cell(12, 20), new Cell(11, 21)], cells["a"]);
    }
}
