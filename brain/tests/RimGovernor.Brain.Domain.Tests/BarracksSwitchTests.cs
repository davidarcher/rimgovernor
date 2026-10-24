using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A standing barracks splits by replan (#3661): three calm colonists on a treeless temperate map (logs at market, so the
/// first plan is one four-bed barracks) keep it when it stands; one pawn near its break gets a bedroom while the barracks
/// keeps the other beds where it stands; and that pawn's mood swinging about the switch line across six replans switches
/// once, since a merge back pays for the built bedroom it gives up (#3675), where a bedroom still on paper merges back.
/// </summary>
public sealed class BarracksSwitchTests
{
    private static readonly double[] Calm = [0.004, 0.004, 0.004];

    /// <summary>The first plan's barracks and rooms all standing, with the colonists' losses a day in a barracks.</summary>
    private static (LayoutPlan Plan, StationFacts Facts) Standing()
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var plan = FirstRoomPlanTests.Derive(f);
        Assert.Single(plan.RoomsOf(PlannedRole.Barracks));
        Assert.Empty(plan.RoomsOf(PlannedRole.Bedroom));
        return (plan, Stand(plan, f with { Derive = false }));
    }

    /// <summary>Every room of the plan standing: a wall on each ring cell and a door on each of its doors.</summary>
    internal static StationFacts Stand(LayoutPlan plan, StationFacts f)
    {
        var ground = new GroundCensus();
        foreach (var r in PlannedRules.PlannedBuiltRooms(plan))
        {
            var doors = plan.ShellDoors(r).ToHashSet();
            foreach (var c in r.WallRing().Cells())
            {
                (doors.Contains(c) ? ground.Doors : ground.Walls).Add(c);
            }
        }
        var built = plan.Rooms.Where(r => !r.Attached()).Select(r => r.Id).ToHashSet();
        return f with { Built = built, ShellStanding = LayoutPlan.ShellStanding(plan, ground) };
    }

    private static StationFacts WithLosses(StationFacts f, double c) => f with { BedMerge = f.BedMerge with { Losses = Known(EquatableArray.From([Calm[0], Calm[1], c])) } };

    private static LayoutPlan Replan(LayoutPlan prev, StationFacts f) =>
        LayoutPlan.ReplanLayout(prev, f with { Plan = prev }, f.Survey, [], new RoomGrowth { Fixed = [] }).Plan;

    private static int Slots(LayoutPlan plan, StationFacts f, PlannedRoom room) => SleepingRules.BedSlotsOf(plan, room, f.Furniture!)?.Count ?? 0;

    private static string Key(LayoutPlan plan, StationFacts f) => (f with { Plan = plan }).InputsKeyParts().Single(p => p.Key == "barracks").Value;

    [Fact]
    public void ACalmColonyKeepsItsStandingBarracks()
    {
        var (plan, f) = Standing();
        var barracks = plan.RoomsOf(PlannedRole.Barracks).Single();

        var next = Replan(plan, WithLosses(f, Calm[2]));

        var kept = Assert.Single(next.RoomsOf(PlannedRole.Barracks));
        Assert.Equal((barracks.Id, barracks.Region.Bounds()), (kept.Id, kept.Region.Bounds()));
        Assert.Equal(Slots(plan, f, barracks), Slots(next, f, kept));
        Assert.Empty(next.RoomsOf(PlannedRole.Bedroom));
    }

    [Fact]
    public void APawnNearItsBreakGetsOneBedroomAndTheBarracksKeepsTheOthers()
    {
        var (plan, f) = Standing();
        var barracks = plan.RoomsOf(PlannedRole.Barracks).Single();
        var slots = Slots(plan, f, barracks);

        // 0.06 capability-days a day over the 60-day horizon outweighs one bedroom's market-wood shell and heat.
        var next = Replan(plan, WithLosses(f, 0.06));

        var kept = Assert.Single(next.RoomsOf(PlannedRole.Barracks));
        Assert.Equal((barracks.Id, barracks.Region.Bounds()), (kept.Id, kept.Region.Bounds()));
        Assert.Equal(slots - 1, Slots(next, f, kept));
        Assert.Single(next.RoomsOf(PlannedRole.Bedroom));
    }

    [Fact]
    public void TheSplitLeavesTheStandingBarracksBuilt()
    {
        var (plan, f) = Standing();
        var barracks = plan.RoomsOf(PlannedRole.Barracks).Single();
        var ground = new GroundCensus();
        foreach (var c in barracks.WallRing().Cells())
        {
            (plan.ShellDoors(barracks).Contains(c) ? ground.Doors : ground.Walls).Add(c);
        }

        var next = Replan(plan, WithLosses(f, 0.06));

        Assert.Single(next.RoomsOf(PlannedRole.Bedroom));
        Assert.Contains(barracks.Id, LayoutPlan.BuiltRooms(next, ground));
    }

    [Fact]
    public void TheSplitKeepsTheStandingBarracksDoorAwayFromTheNewRooms()
    {
        var (first, f) = Standing();
        var b = first.RoomsOf(PlannedRole.Barracks).Single();
        var bounds = b.Region.Bounds();
        var door = new Cell(bounds.X + bounds.Width, bounds.Z + (bounds.Height / 2));
        var plan = first with { Rooms = [.. first.Rooms.Select(r => r.Id == b.Id ? r with { Door = door, DoorRot = InteriorRules.DoorSide(bounds, door).Item1 } : r)] };
        var ground = new GroundCensus();
        var barracks = plan.RoomsOf(PlannedRole.Barracks).Single();
        foreach (var c in barracks.WallRing().Cells())
        {
            (plan.ShellDoors(barracks).Contains(c) ? ground.Doors : ground.Walls).Add(c);
        }

        var next = Replan(plan, WithLosses(Stand(plan, f), 0.06));

        Assert.Single(next.RoomsOf(PlannedRole.Bedroom));
        Assert.Equal(door, next.Rooms.Single(r => r.Id == barracks.Id).Door);
        Assert.Contains(barracks.Id, LayoutPlan.BuiltRooms(next, ground));
    }

    [Fact]
    public void AMoodSwingingAboutTheSwitchLineSwitchesAtMostOnceInSixReplans()
    {
        var (plan, f) = Standing();

        // The switch line: the least loss a day at which the standing barracks gives up a bed.
        double lo = 0, hi = 1;
        for (var i = 0; i < 40; i++)
        {
            var mid = (lo + hi) / 2;
            if (Key(plan, WithLosses(f, mid)) == Key(plan, WithLosses(f, Calm[2])))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        double above = hi * 1.1, below = hi * 0.9;
        Assert.NotEqual(Key(plan, WithLosses(f, below)), Key(plan, WithLosses(f, above)));

        // Each review's plan is built before the next: the bedroom the switch plans stands when the mood swings back.
        var bedrooms = new List<int>();
        var cur = plan;
        var standing = f;
        for (var review = 0; review < 6; review++)
        {
            cur = Replan(cur, WithLosses(standing, review % 2 == 0 ? above : below));
            standing = Stand(cur, standing);
            bedrooms.Add(cur.RoomsOf(PlannedRole.Bedroom).Count);
        }

        var switches = bedrooms.Prepend(0).Zip(bedrooms, (a, b) => a != b).Count(s => s);
        Assert.True(switches == 1, $"bedrooms per replan {string.Join(",", bedrooms)}");
    }

    [Fact]
    public void ABedroomStillOnPaperMergesBackWhenTheMoodCalms()
    {
        var (plan, f) = Standing();
        var barracks = plan.RoomsOf(PlannedRole.Barracks).Single();
        var split = Replan(plan, WithLosses(f, 0.06));
        Assert.Single(split.RoomsOf(PlannedRole.Bedroom));

        // Nothing of the bedroom stands: merging back gives up no spend, and the calm sleeper's loss is less than the room saved.
        var next = Replan(split, WithLosses(f, Calm[2]));

        Assert.Empty(next.RoomsOf(PlannedRole.Bedroom));
        var kept = Assert.Single(next.RoomsOf(PlannedRole.Barracks));
        Assert.Equal(Slots(plan, f, barracks), Slots(next, f, kept));
    }
}
