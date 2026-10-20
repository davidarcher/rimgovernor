using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The tomb and morgue join the station graph only once the burial demand asks for them or the plan already holds
/// one: a first plan with no dead holds neither, and a standing one is never left without a node to retire it.
/// </summary>
public sealed class OutskirtsRoomNodesTests
{
    private static PlannedRoom Room(int id, PlannedRole role) => new()
    {
        Id = id,
        Role = role,
        Region = RegionRules.RectRegion(new Rectangle { X = 10 * id, Z = 10, Width = 5, Height = 4 }),
    };

    [Fact]
    public void NoDeadAndNoHeldRoomOwesNoOutskirtsRoom()
    {
        Assert.Empty(LayoutPlan.OutskirtsRoomNodes(new LayoutPlan(), new RoomDemand()));
    }

    [Fact]
    public void BurialDemandAsksForTheTombAndAColdMorgue()
    {
        var nodes = LayoutPlan.OutskirtsRoomNodes(new LayoutPlan(), new RoomDemand { Tomb = 1, Morgue = 1 });

        Assert.Equal(["tomb", "morgue"], nodes.Select(n => n.Id));
        Assert.False(nodes[0].Cold);
        Assert.True(nodes[1].Cold);
        Assert.All(nodes, n => Assert.Equal(LayoutOutskirtsRules.ReasonOutskirtsRoom, n.Reason));
    }

    [Fact]
    public void HeldRoomsStayNodesOnceTheDemandEnds()
    {
        var plan = new LayoutPlan { Rooms = [Room(1, PlannedRole.Tomb), Room(2, PlannedRole.Morgue)], NextRoomId = 3 };

        var nodes = LayoutPlan.OutskirtsRoomNodes(plan, new RoomDemand());

        Assert.Equal(["tomb", "morgue"], nodes.Select(n => n.Id));
    }

    [Fact]
    public void AFullTombAsksForASecond()
    {
        var plan = new LayoutPlan { Rooms = [Room(1, PlannedRole.Tomb)], NextRoomId = 2 };

        var nodes = LayoutPlan.OutskirtsRoomNodes(plan, new RoomDemand { Tomb = 2 });

        Assert.Equal(["tomb", "tomb-2"], nodes.Select(n => n.Id));
    }
}
