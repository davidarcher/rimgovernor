using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A built room a replan drops leaves its ground to clearance (#3681): the treeless temperate first plan stands, with a
/// built hospital beside it that the colony (no patient) has no group for and does not hold. The replan drops the
/// hospital and its ring and interior become the plan's RetiredGround. Retired is omitted, not unneeded (task 8.1): its idle
/// bed is a compared transition, kept while its service is unassessed together with the walls around it, which come down
/// only once nothing on the ground is retained.
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
            DoorSide = new DoorSide.Doored(Rotation.West),
        };
        var prev = first with { Rooms = [.. first.Rooms, hospital], NextRoomId = first.NextRoomId + 1 };
        Assert.True(prev.Valid());

        var stood = BarracksSwitchTests.Stand(prev, f with { Derive = false, Plan = prev });
        // A built room is started (FixedRooms starts from the built rooms).
        var next = LayoutPlan.ReplanLayout(prev, stood, f.Survey, [], new RoomGrowth { Fixed = [.. stood.Built!] }).Plan;

        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
        var ground = RegionRules.RectRegion(ClearanceGroundRules.RoomGround(hospital.Region.Bounds()));
        Assert.Contains(next.RetiredGround, g => g.Equal(ground));
        Assert.True(ClearanceGroundRules.RetiredGroundOf(next).Has(ground));

        var bed = new ClearanceTarget { EntityId = "bed", DefName = "HospitalBed", Minimum = new Cell(east + 1, south + 1), Maximum = new Cell(east + 1, south + 1), Player = true, Deconstructible = true };
        var wall = new ClearanceTarget { EntityId = "wall", DefName = "Wall", Minimum = new Cell(east + 1, south - 1), Maximum = new Cell(east + 1, south - 1), Player = true, Deconstructible = true, EnclosesRoom = true };
        var facts = TransitionFixtures.Facts(TransitionFixtures.Mechanics("bed", "HospitalBed", bed.Minimum));
        // The idle bed and its walls stay: nothing is owed, so the plan drops the entry without demolishing anything.
        Assert.Contains(ClearanceGroundRules.RetiredGroundDone(next, [bed, wall], [], facts), g => g.Equal(ground));
        Assert.Contains(ClearanceGroundRules.RetiredGroundDone(next, [bed, wall], [], new TransitionFacts()), g => g.Equal(ground));
        // With no building retained, the walls are owed.
        Assert.DoesNotContain(ClearanceGroundRules.RetiredGroundDone(next, [wall], [], facts), g => g.Equal(ground));
    }

    /// <summary>
    /// A started room the replan drops is retired too (#3742): the hospital has one wall blueprint and no built wall,
    /// so it is started (growth.Fixed) but not built; its ground is retired for clearance to cancel the blueprint.
    /// The same hospital untouched (nothing of ours on its walls) leaves no ground behind.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReplanDroppingAStartedRoomRetiresItsGround(bool blueprint)
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var first = FirstRoomPlanTests.Derive(f);
        var east = first.Rooms.Max(r => r.Region.Bounds().X + r.Region.Bounds().Width) + 4;
        var south = first.Rooms.Min(r => r.Region.Bounds().Z);
        var hospital = new PlannedRoom
        {
            Id = first.NextRoomId,
            Role = PlannedRole.Hospital,
            Region = RegionRules.RectRegion(new Rectangle(east, south, 3, 3)),
            Door = new Cell(east - 1, south + 1),
            DoorSide = new DoorSide.Doored(Rotation.West),
        };
        var prev = first with { Rooms = [.. first.Rooms, hospital], NextRoomId = first.NextRoomId + 1 };
        // The first plan's rooms stand; the hospital is not built.
        var stood = BarracksSwitchTests.Stand(first, f with { Derive = false });
        Assert.DoesNotContain(hospital.Id, stood.Built!);
        // One wall blueprint on the hospital's south wall.
        HashSet<Cell> occupied = blueprint ? [new Cell(east + 1, south - 1)] : [];
        var started = LayoutPlan.FixedRooms(prev, new GroundCensus(), occupied);
        started.UnionWith(stood.Built!);
        Assert.Equal(blueprint, started.Contains(hospital.Id));

        var next = LayoutPlan.ReplanLayout(prev, stood with { Plan = prev }, f.Survey, [], new RoomGrowth { Fixed = started }).Plan;

        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
        var ground = RegionRules.RectRegion(ClearanceGroundRules.RoomGround(hospital.Region.Bounds()));
        Assert.Equal(blueprint, next.RetiredGround.Any(g => g.Equal(ground)));
    }
}
