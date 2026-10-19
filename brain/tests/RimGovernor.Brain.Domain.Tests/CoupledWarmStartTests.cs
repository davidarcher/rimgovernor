using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3556: the previous coupled selection seeds the next search even after a tiled replan renumbered its planned rooms.
public sealed class CoupledWarmStartTests
{
    private static PlannedRoom Room(int id, PlannedRole role, int x, int z) => new() { Id = id, Role = role, Region = RegionRules.RectRegion(new Rectangle(x, z, 4, 4)) };

    [Fact]
    public void ARenumberedRoomIsFoundAgainByItsRoleAndGround()
    {
        EquatableArray<PlannedRoom> before = [Room(5, PlannedRole.Bedroom, 10, 10), Room(6, PlannedRole.Kitchen, 20, 10), Room(7, PlannedRole.Bedroom, 30, 10)];
        // Room 5 re-tiled as 35 on the same ground; 6 kept its id; 7 re-tiled as 37 and 38 both overlapping (the lowest id wins); nothing of another role on 5's ground counts.
        EquatableArray<PlannedRoom> after = [Room(35, PlannedRole.Bedroom, 11, 11), Room(6, PlannedRole.Kitchen, 20, 10), Room(38, PlannedRole.Bedroom, 29, 9),
            Room(37, PlannedRole.Bedroom, 31, 11), Room(40, PlannedRole.Kitchen, 10, 10)];
        EquatableArray<string> previous = ["planned/room5/door/Steel/composition/15", "planned/room6/Steel/Full/composition/2", "planned/room7/build/Bed/147/85/composition/6",
            "planned/room9/wall/composition/1", "service/rest/pawn/composition/3"];
        var warm = RoundsRules.WarmStartOf(previous, before, after);
        Assert.Equal(["planned/room35/door/Steel/composition/15", "planned/room6/Steel/Full/composition/2", "planned/room37/build/Bed/147/85/composition/6",
            "planned/room9/wall/composition/1", "service/rest/pawn/composition/3"], warm);
    }

    [Fact]
    public void ARoomWithNoSuccessorOnItsGroundKeepsItsId()
    {
        EquatableArray<PlannedRoom> before = [Room(5, PlannedRole.Bedroom, 10, 10)];
        EquatableArray<string> previous = ["planned/room5/door/Steel/composition/15"];
        Assert.Equal(previous, RoundsRules.WarmStartOf(previous, before, [Room(35, PlannedRole.Bedroom, 50, 50)]));
        Assert.Equal(previous, RoundsRules.WarmStartOf(previous, before, []));
        Assert.Equal(previous, RoundsRules.WarmStartOf(previous, [], [Room(35, PlannedRole.Bedroom, 10, 10)]));
    }
}
