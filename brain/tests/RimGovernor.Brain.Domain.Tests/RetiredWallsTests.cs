using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A retired hut's walls come down once its furniture is gone (#3681): the clearance step reads the room census to
/// tell a wall that still bounds a standing room (it waits) from the retired room's own ring. The home-upkeep
/// reader's colony read carries that census; an unread one is no step, not a crash.
/// </summary>
public sealed class RetiredWallsTests
{
    // A 5x5 hut ring at (10,10), its 3x3 interior enclosed and roofed.
    private static readonly Rectangle Hut = new() { X = 10, Z = 10, Width = 5, Height = 5 };

    private static readonly LayoutPlan Plan = new()
    {
        NextRoomId = 2,
        Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Storage, Region = RegionRules.RectRegion(new Rectangle(30, 30, 3, 3)), Door = new Cell(29, 31), DoorSide = new DoorSide.Doored(Rotation.West) }],
        RetiredGround = [RegionRules.RectRegion(Hut)],
    };

    private static List<ClearanceTarget> Walls() =>
    [
        .. PlannedRules.RectCells(Hut)
            .Where(c => c.X == Hut.X || c.Z == Hut.Z || c.X == Hut.X + Hut.Width - 1 || c.Z == Hut.Z + Hut.Height - 1)
            .Select(c => new ClearanceTarget { EntityId = $"Thing_Wall{c.X}_{c.Z}", DefName = "Wall", Minimum = c, Maximum = c, Player = true, Deconstructible = true, EnclosesRoom = true }),
    ];

    private static RoomObservation Rooms() => RoomFixtures.Census((RoomFixtures.State("hut"), PlannedRules.RectCells(new Rectangle(Hut.X + 1, Hut.Z + 1, 3, 3))));

    private static ColonyProjection Colony(Fact<RoomObservation> rooms) => ColonyFactsCoreTests.Projection() with
    {
        LayoutPlan = Known(Plan),
        Rooms = rooms,
        Facts = new() { CurrentConstruction = CurrentConstruction.Empty },
    };

    [Fact]
    public void TheRetiredHutsRingComesDownWithItsRoof()
    {
        var walls = Walls();

        var (step, ok) = HomeUpkeepReader.PlannedGroundStep(Colony(Known(Rooms())), walls, [], null!);

        Assert.True(ok);
        Assert.Equal(GroundPhase.Walls, step.Phase);
        Assert.Equal(walls.Select(w => w.EntityId).Order(StringComparer.Ordinal), step.Targets.Items.Select(t => t.EntityId).Order(StringComparer.Ordinal));
        Assert.Equal(9, step.Roof.Count);
    }

    [Fact]
    public void ALastRoofHolderTakesTheGroundsRoofOff()
    {
        // The ring is down but for its door, which encloses nothing now and still holds the roof auto-roof laid
        // over the ring: its step is the walls step, with the roof of the whole hut ground (#3681).
        var door = new ClearanceTarget { EntityId = "Thing_Door12_10", DefName = "Door", Minimum = new Cell(12, 10), Maximum = new Cell(12, 10), Player = true, Deconstructible = true, Designated = true, RoofBlocker = "roof would collapse" };

        var (step, ok) = HomeUpkeepReader.PlannedGroundStep(Colony(Known(RoomObservation.NoRooms)), [door], [], null!);

        Assert.True(ok);
        Assert.Equal(GroundPhase.Walls, step.Phase);
        Assert.Equal(25, step.Roof.Count);
        var pending = HomeUpkeepReader.PendingClearanceStep(step);
        Assert.Empty(pending.Targets);
        Assert.Equal(25, pending.Roof.Count);
    }

    [Fact]
    public void AnUnreadRoomCensusIsNoStep()
    {
        var walls = Walls();

        var (_, ok) = HomeUpkeepReader.PlannedGroundStep(Colony(Unknown<RoomObservation>()), walls, [], null!);

        Assert.False(ok);
        Assert.Empty(HomeUpkeepReader.PlannedGroundWork(Colony(Unknown<RoomObservation>()), walls, [], null!));
    }
}
