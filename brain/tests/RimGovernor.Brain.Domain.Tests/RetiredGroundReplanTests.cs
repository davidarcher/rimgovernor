using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A built room a replan drops leaves its ground to clearance (#3681): the treeless temperate first plan stands, with a
/// built hospital beside it that the colony (no patient) has no group for and does not hold. The replan drops the
/// hospital and its ring and interior become the plan's RetiredGround, which planned-ground clearance then takes down.
/// </summary>
public sealed class RetiredGroundReplanTests
{
    [Fact]
    public void AReplanDroppingABuiltRoomRetiresItsGround()
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var first = FirstRoomPlanTests.Derive(f);
        // A 3x3 hospital east of every planned room, on open survey ground.
        var east = first.Rooms.Max(r => r.Region.Bounds().X + r.Region.Bounds().Width) + 4;
        var south = first.Rooms.Min(r => r.Region.Bounds().Z);
        var hospital = new PlannedRoom
        {
            Id = first.NextRoomId,
            Role = PlannedRole.Hospital,
            Region = RegionRules.RectRegion(new Rectangle(east, south, 3, 3)),
            Door = new Cell(east - 1, south + 1),
            DoorRot = Rotation.West,
        };
        var prev = first with { Rooms = [.. first.Rooms, hospital], NextRoomId = first.NextRoomId + 1 };
        Assert.True(prev.Valid());

        var next = LayoutPlan.ReplanLayout(prev, BarracksSwitchTests.Stand(prev, f with { Derive = false, Plan = prev }), f.Survey, [], new RoomGrowth { Fixed = [] }).Plan;

        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
        var ground = RegionRules.RectRegion(ClearanceGroundRules.RoomGround(hospital.Region.Bounds()));
        Assert.Contains(next.RetiredGround, g => g.Equal(ground));
        Assert.True(ClearanceGroundRules.RetiredGroundOf(next).Has(ground));
    }
}
