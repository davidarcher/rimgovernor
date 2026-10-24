using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Replan hysteresis for unbuilt rooms (#3740, D1 of #3738): the temperate first plan of three colonists (bedrooms,
/// lab, circulation and yard), nothing built, replanned. Over the same facts every room keeps its site and id. A
/// barren patch (soil that costs nothing to build on) that gives the yard a site more than ReplanGain better moves the
/// yard, under a new id, and at most a room that comes to share its wall; one that gives the lab a site less than
/// ReplanGain better moves nothing. A role that left the demand still leaves, and the base closes the hole it leaves
/// when that saves more than ReplanGain of walls; a new one still gets a room while the rest is kept.
/// </summary>
public sealed class ReplanHysteresisTests
{
    private static StationFacts Facts() => FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild");

    /// <summary>The survey with the patch's soil barren: no fertility, so no soil cost to build on.</summary>
    private static MapSurvey Barren(MapSurvey s, Rectangle patch) => s with
    {
        Cells = [.. s.Cells.Select(c => patch.X <= c.Cell.X && c.Cell.X < patch.X + patch.Width && patch.Z <= c.Cell.Z && c.Cell.Z < patch.Z + patch.Height ? c with { Fertility = 0 } : c)],
    };

    /// <summary>The replan of first over f (nothing built, nothing held), with its advice.</summary>
    private static (LayoutPlan Plan, StationAdvice Advice) Replan(LayoutPlan first, StationFacts f) =>
        LayoutPlan.ReplanLayout(first, f with { Derive = false, Plan = first }, f.Survey, [], new RoomGrowth { Fixed = [] });

    private static Dictionary<int, Rectangle> Sites(LayoutPlan plan) => plan.Rooms.Where(r => !r.Attached()).ToDictionary(r => r.Id, r => r.Region.Bounds());

    [Fact]
    public void AReplanOverTheSameFactsKeepsEveryUnbuiltRoomsSiteAndId()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f);
        Assert.True(first.Rooms.Count(r => !r.Attached()) >= 3);

        var (next, advice) = Replan(first, f);

        Assert.Equal(Sites(first), Sites(next));
        Assert.Equal(LayoutEmbedRules.ReplanKept, advice.Embedding.Replan!.Verdict);
        Assert.Equal(LayoutEmbedRules.ReasonReplanGainBelow, advice.Embedding.Replan.Reason);
        var diff = LayoutPlan.DiffReplanTile(first, new HashSet<int>(), advice);
        Assert.All(diff.Rooms, d => Assert.Equal((LayoutReplanTiledRules.ReasonDiffSited, LayoutReplanTiledRules.IdKept, d.Room), (d.Reason, d.Identity, d.Tiled)));
    }

    [Fact]
    public void ASiteBetterByMoreThanReplanGainMovesThatRoomAndOnlyARoomThatJoinsItsWall()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f);
        var yard = first.RoomsOf(PlannedRole.Yard).Single();
        var at = yard.Region.Bounds();
        // Barren ground of the yard's size east of the bedrooms.
        var patch = new Rectangle(45, at.Z, at.Width, at.Height);

        var (next, advice) = Replan(first, f with { Survey = Barren(f.Survey, patch) });

        var replan = advice.Embedding.Replan!;
        Assert.Equal((LayoutEmbedRules.ReplanReplaced, LayoutEmbedRules.ReasonReplanGainAbove), (replan.Verdict, replan.Reason));
        Assert.True(replan.Held - replan.Tiled >= PlanWeights.ReplanGain);
        var moved = next.RoomsOf(PlannedRole.Yard).Single();
        Assert.Equal(patch, moved.Region.Bounds());
        Assert.True(moved.Id >= first.NextRoomId);
        var kept = Sites(first);
        kept.Remove(yard.Id);
        var after = Sites(next);
        after.Remove(moved.Id);
        // Every other room keeps its site and id, save one the replan stands against the moved yard's wall: the
        // enclosure is in the objective (#3746), so the room that shares the yard's wall saves the walls it would
        // otherwise need.
        var gone = kept.Keys.Where(id => !after.ContainsKey(id)).ToList();
        var come = after.Where(a => !kept.ContainsKey(a.Key)).ToList();
        Assert.True(gone.Count <= 1, $"{gone.Count} rooms moved besides the yard");
        Assert.Equal(gone.Count, come.Count);
        Assert.All(come, a => Assert.Equal(1, LayoutEmbedRules.GapDist(a.Value, moved.Region.Bounds())));
        Assert.All(kept.Where(k => after.ContainsKey(k.Key)), k => Assert.Equal(k.Value, after[k.Key]));
        var diff = LayoutPlan.DiffReplanTile(first, new HashSet<int>(), advice);
        Assert.Equal(LayoutReplanTiledRules.IdNew, diff.Rooms.Single(d => d.Room == yard.Id).Identity);
        Assert.All(diff.Rooms.Where(d => d.Room != yard.Id && !gone.Contains(d.Room)), d => Assert.Equal(LayoutReplanTiledRules.IdKept, d.Identity));
    }

    [Fact]
    public void ASiteBetterByLessThanReplanGainMovesNothing()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f);
        var lab = first.RoomsOf(PlannedRole.Lab).Single().Region.Bounds();
        // Barren ground of the lab's size just east of it: worth less than ReplanGain.
        var patch = new Rectangle(lab.X + lab.Width, lab.Z, lab.Width, lab.Height);

        var (next, advice) = Replan(first, f with { Survey = Barren(f.Survey, patch) });

        var replan = advice.Embedding.Replan!;
        Assert.True(replan.Tiled < replan.Held, "the new embedding scores better");
        Assert.True(replan.Held - replan.Tiled < PlanWeights.ReplanGain);
        Assert.Equal((LayoutEmbedRules.ReplanKept, LayoutEmbedRules.ReasonReplanGainBelow), (replan.Verdict, replan.Reason));
        Assert.Equal(Sites(first), Sites(next));
    }

    [Fact]
    public void ARoleThatLeftTheDemandIsRetiredAndTheBaseClosesTheHoleItLeaves()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f with { GoalBuild = new GoalBuildDemand { HospitalBeds = 1 } });
        var hospital = first.RoomsOf(PlannedRole.Hospital).Single();

        var (next, advice) = Replan(first, f);

        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
        Assert.Empty(next.RoomsOf(PlannedRole.Hospital));
        // The held rooms stand around the hospital's empty site; the enclosure is in the objective (#3746), so the
        // new embedding, which closes up the base, saves more than ReplanGain of walls and replaces them.
        var replan = advice.Embedding.Replan!;
        Assert.Equal((LayoutEmbedRules.ReplanReplaced, LayoutEmbedRules.ReasonReplanGainAbove), (replan.Verdict, replan.Reason));
        Assert.True(replan.Held - replan.Tiled >= PlanWeights.ReplanGain);
    }

    [Fact]
    public void ANewRoleGetsANewRoomWhileTheRestIsKept()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f);
        Assert.Empty(first.RoomsOf(PlannedRole.Hospital));

        var (next, advice) = Replan(first, f with { GoalBuild = new GoalBuildDemand { HospitalBeds = 1 } });

        Assert.Equal(LayoutEmbedRules.ReplanKept, advice.Embedding.Replan!.Verdict);
        Assert.True(next.RoomsOf(PlannedRole.Hospital).Single().Id >= first.NextRoomId);
        AssertKept(first, next, PlannedRole.Lab, PlannedRole.Yard);
    }

    /// <summary>The rooms of the roles stand where they stood under their ids.</summary>
    private static void AssertKept(LayoutPlan first, LayoutPlan next, params PlannedRole[] roles)
    {
        foreach (var role in roles)
        {
            var was = first.RoomsOf(role).Single();
            var now = next.RoomsOf(role).Single();
            Assert.Equal((was.Id, was.Region.Bounds()), (now.Id, now.Region.Bounds()));
        }
    }
}
