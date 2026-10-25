using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayoutCandidateIntegrationTests
{
    [Fact]
    public void NativeClosureBatchKeepsTwoTrialsOfTheSameRoomAndTheirProjectedRoutesSeparate()
    {
        var from = new Cell(1, 2);
        var to = new Cell(3, 4);
        var first = PlannedRoomPredictionTests.Closed(ColonyReview.CandidateKey("regroup:1", "1"), "WoodLog", "Bedroom", 30, 2, 9, 0, false) with
        {
            Routes = [new("pawn", from, to, new RouteTicks.Reachable(17)) { FromStart = false, Projection = "regroup:1/1/WoodLog" }],
        };
        var second = PlannedRoomPredictionTests.Closed(ColonyReview.CandidateKey("regroup:10", "1"), "WoodLog", "Laboratory", 70, 4, 20, 0, false) with
        {
            Routes = [new("pawn", from, to, RouteTicks.None) { FromStart = false, Projection = "regroup:10/1/WoodLog" }],
        };

        var bedroom = Assert.Single(ColonyReview.CandidatePredictions("regroup:1", [second, first]));
        var laboratory = Assert.Single(ColonyReview.CandidatePredictions("regroup:10", [second, first]));

        Assert.Equal(("1", "Bedroom", 2), (bedroom.Room, bedroom.Role, bedroom.Stage));
        Assert.Equal(("1", "Laboratory", 4), (laboratory.Room, laboratory.Role, laboratory.Stage));
        Assert.Equal("1/WoodLog", Assert.Single(bedroom.Routes).Projection);
        Assert.Equal(17d, Assert.IsType<RouteTicks.Reachable>(Assert.Single(bedroom.Routes).Ticks).Ticks);
        Assert.IsType<RouteTicks.Unreachable>(Assert.Single(laboratory.Routes).Ticks);
    }

    [Fact]
    public void CandidateProjectionRetainsNativeFactsAndFullStationGeometryButDropsPriorPlanDerivedFacts()
    {
        var colony = ColonyFactsCoreTests.Reading();
        var old = new LayoutPlan();
        var plan = FirstLayoutPlanTests.FirstPlan("mild", 3);
        colony = colony with { Facts = colony.Facts with
        {
            LayoutPlan = old,
            PlannedRoomPredictions = [PlannedRoomPredictionTests.Closed("999", "WoodLog", "Bedroom", 30, 2, 9, 0, false)],
            PlannedSites = new PlannedSiteFacts(),
        } };

        var projected = ColonyReview.ProjectLayoutCandidate(colony, plan, null);

        Assert.Same(plan, projected.Facts.LayoutPlan);
        Assert.Same(colony.Facts.Census, projected.Facts.Census);
        Assert.Same(colony.Facts.CurrentConstruction, projected.Facts.CurrentConstruction);
        Assert.Same(colony.Facts.ForecastObserved, projected.Facts.ForecastObserved);
        Assert.Equal(plan.Rooms.Where(r => !r.Outdoor), projected.Facts.PlannedShelters);
        Assert.Equal(plan.Links, projected.Facts.PlannedLinks);
        Assert.NotEmpty(plan.Pieces);
        Assert.Empty(projected.Facts.PlannedRoomPredictions);
        Assert.Null(projected.Facts.PlannedSites);
        Assert.Null(projected.Facts.CoupledAlternatives);
        Assert.Same(old, colony.Facts.LayoutPlan);
    }

    [Fact]
    public void FirstPlanCandidateTilesTheRetainedGroupingWithTheCanonicalFirstPlanReservations()
    {
        var facts = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild");
        var prepared = LayoutDeriveRules.FirstPlanFacts(facts.Survey, facts, []);
        var grouping = LayoutRegroupRules.Seed(LayoutStationPlanRules.RegroupInputOf(prepared));
        var advised = LayoutStationPlanRules.AdviseStations(prepared, grouping);

        var (plan, tiled) = LayoutDeriveRules.TileFirstPlan(facts.Survey, prepared, advised);

        Assert.NotNull(plan);
        Assert.Same(grouping.Embedding, tiled.Embedding);
        Assert.Same(grouping.Clustering, tiled.Clustering);
        Assert.NotEmpty(plan.Rooms);
        Assert.NotEmpty(plan.Pieces);
        Assert.Equal(facts.Survey.Cold, plan.Cold);
        Assert.Equal(facts.Survey.Hot, plan.Hot);
    }
}
