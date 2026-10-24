using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Started rooms are pinned (#3741, D2 of #3738): a room with anything of ours on it (growth.Fixed) is in the
/// embedding's pin set, so a replan keeps its id and site while it has a node; one whose role left the demand has no
/// node and leaves the plan, since only the rooms the colony holds are kept without one.
/// </summary>
public sealed class StartedRoomPinTests
{
    private static PlannedRoom Room(int id) => new()
    {
        Id = id,
        Role = PlannedRole.Storage,
        Region = RegionRules.RectRegion(new Rectangle(10 * id, 10, 4, 4)),
    };

    [Fact]
    public void ThePinSetIsTheStartedHeldSleptAndInUseRooms()
    {
        var plan = new LayoutPlan { Rooms = [.. Enumerable.Range(1, 6).Select(Room)], NextRoomId = 7 };
        var growth = new RoomGrowth { Fixed = [1], InFlight = [2], Slept = [3], InUse = [4] };

        Assert.Equal([1, 2, 3, 4], LayoutPlan.TilePinned(plan, growth).Order());
        // Only the rooms the colony holds are kept without a node.
        Assert.Equal([2, 3, 4], LayoutPlan.HeldRooms(plan, growth).Order());
    }

    [Fact]
    public void AnUnknownCensusPinsEveryRoom()
    {
        var plan = new LayoutPlan { Rooms = [.. Enumerable.Range(1, 6).Select(Room)], NextRoomId = 7 };

        Assert.Equal([1, 2, 3, 4, 5, 6], LayoutPlan.TilePinned(plan, new RoomGrowth { InFlight = [2] }).Order());
    }

    /// <summary>The facts and growth of a replan of prev with every room but the unbuilt one standing and one wall blueprint on each of started.</summary>
    private static (StationFacts Facts, RoomGrowth Growth) Census(LayoutPlan prev, StationFacts f, int unbuilt, params PlannedRoom[] started)
    {
        var ground = new GroundCensus();
        foreach (var r in PlannedRules.PlannedBuiltRooms(prev).Where(r => r.Id != unbuilt))
        {
            var doors = prev.ShellDoors(r).ToHashSet();
            foreach (var c in r.WallRing().Cells())
            {
                (doors.Contains(c) ? ground.Doors : ground.Walls).Add(c);
            }
        }
        var occupied = started.Select(r => r.WallRing().Cells().First(c => !prev.ShellDoors(r).Contains(c))).ToHashSet();
        var built = prev.Rooms.Where(r => !r.Attached() && r.Id != unbuilt).Select(r => r.Id).ToHashSet();
        var growth = new RoomGrowth { Fixed = LayoutPlan.FixedRooms(prev, ground, occupied) };
        Assert.All(started, r => Assert.Contains(r.Id, growth.Fixed));
        return (f with { Derive = false, Plan = prev, Built = built, ShellStanding = LayoutPlan.ShellStanding(prev, ground) }, growth);
    }

    [Fact]
    public void AStartedRoomKeepsItsIdAndSiteWhenABetterSiteExists()
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var first = FirstRoomPlanTests.Derive(f);
        // One room moved east of every other onto open ground: its site in the first plan is the better one.
        var room = first.Rooms.First(r => !r.Attached() && first.ChildrenOf(r.Id).Count == 0 && r.Role != PlannedRole.Barracks);
        var dx = first.Rooms.Max(r => r.Region.Bounds().X + r.Region.Bounds().Width) + 6 - room.Region.Bounds().X;
        var b = room.Region.Bounds();
        var moved = room with
        {
            Region = RegionRules.RectRegion(new Rectangle(b.X + dx, b.Z, b.Width, b.Height)),
            Door = new Cell(room.Door.X + dx, room.Door.Z),
        };
        var prev = first with
        {
            Rooms = [.. first.Rooms.Select(r => r.Id == room.Id ? moved : r)],
            Links = [.. first.Links.Where(l => l.A != room.Id && l.B != room.Id)],
            // The entrances and conduits are the tiler's and laid again by the replan.
            Entrances = [],
            Conduits = [],
            Pieces = first.Pieces.Where(e => e.Key != room.Id).ToDictionary(e => e.Key, e => e.Value),
            Lamps = first.Lamps.Where(e => e.Key != room.Id).ToDictionary(e => e.Key, e => e.Value),
        };
        Assert.True(prev.Valid());

        // Untouched, the room is sited again away from where it stands.
        var (free, untouched) = Census(prev, f, room.Id);
        var resited = LayoutPlan.ReplanLayout(prev, free, f.Survey, [], untouched).Plan;
        Assert.DoesNotContain(resited.Rooms, r => r.Id == room.Id && r.Region.Bounds() == moved.Region.Bounds());

        // One wall blueprint on its ring pins it: same id, same site.
        var (pinned, started) = Census(prev, f, room.Id, moved);
        var next = LayoutPlan.ReplanLayout(prev, pinned, f.Survey, [], started).Plan;
        var kept = Assert.Single(next.Rooms, r => r.Id == room.Id);
        Assert.Equal(moved.Region.Bounds(), kept.Region.Bounds());
        Assert.Equal(room.Role, kept.Role);
    }

    [Fact]
    public void AStartedRoomWhoseRoleLeftTheDemandLeavesThePlan()
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var first = FirstRoomPlanTests.Derive(f);
        // A 3x3 hospital east of every planned room that the colony (no patient) has no group for, its first wall a blueprint.
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
        var (facts, growth) = Census(prev, f, hospital.Id, hospital);
        Assert.Contains(hospital.Id, LayoutPlan.TilePinned(prev, growth));

        var next = LayoutPlan.ReplanLayout(prev, facts, f.Survey, [], growth).Plan;

        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
    }
}
