using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Replan hysteresis for unbuilt rooms (#3740, D1 of #3738): the temperate first plan of three colonists (bedrooms,
/// lab, circulation and yard), nothing built, replanned. Over the same facts every room keeps its site and id. A
/// barren patch (soil that costs nothing to build on) that gives the yard a site more than ReplanGain better moves the
/// yard alone, under a new id; one that gives the lab a site less than ReplanGain better moves nothing. A role that
/// left the demand still leaves, and a new one still gets a room, while the rest is kept.
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
    public void ASiteBetterByMoreThanReplanGainMovesOnlyThatRoom()
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
        Assert.Equal(kept, after);
        var diff = LayoutPlan.DiffReplanTile(first, new HashSet<int>(), advice);
        Assert.Equal(LayoutReplanTiledRules.IdNew, diff.Rooms.Single(d => d.Room == yard.Id).Identity);
        Assert.All(diff.Rooms.Where(d => d.Room != yard.Id), d => Assert.Equal(LayoutReplanTiledRules.IdKept, d.Identity));
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
    public void ARoleThatLeftTheDemandIsRetiredWhileTheRestIsKept()
    {
        var f = Facts();
        var first = FirstRoomPlanTests.Derive(f with { GoalBuild = new GoalBuildDemand { HospitalBeds = 1 } });
        var hospital = first.RoomsOf(PlannedRole.Hospital).Single();

        var (next, advice) = Replan(first, f);

        Assert.Equal(LayoutEmbedRules.ReplanKept, advice.Embedding.Replan!.Verdict);
        Assert.DoesNotContain(next.Rooms, r => r.Id == hospital.Id);
        Assert.Empty(next.RoomsOf(PlannedRole.Hospital));
        AssertKept(first, next, PlannedRole.Lab, PlannedRole.Yard);
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
