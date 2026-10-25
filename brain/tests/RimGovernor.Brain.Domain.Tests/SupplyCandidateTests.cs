using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3906: a supply candidate reads whole. Each fact a builder could not read is a named state (an Unpriced yield or
// work, an Unscheduled lead, an Unwalked path, Unread storage, an Unmeasured ground), the planner names it in its
// finding, and wherever the facts are known the ranking is the one the planner always made.
public sealed class SupplyCandidateTests
{
    private static readonly ResourceKey Steel = new() { Def = new Resource("Steel") };

    private static SupplyCandidate Food(string id, YieldSupply supply, CandidateLead lead, CandidateWork work) =>
        CandidateRules.FoodCandidate(CandidateKind.Crop, id, supply, CandidateState.Closed, lead, work);

    private static SupplyCandidate Haul(string id, long count, YieldStorage storage, CandidatePath path) =>
        CandidateRules.SourceCandidate(CandidateKind.Loot, id, 0, path, true, 75, CandidateRules.SourceYield(Steel, count, 0, storage));

    private static SupplyPlan Plan(SupplyDemand demand, params SupplyCandidate[] candidates) =>
        SupplyPlanRules.PlanSupply(new SupplyPlanRequest { Demands = [demand], Candidates = [.. candidates], Labor = 1e6 });

    private static readonly SupplyDemand Nutrition = new() { Good = SupplyPlanRules.NutritionKey, PerDay = 10, HorizonDays = 10 };

    private static readonly SupplyDemand SteelDeficit = new() { Good = Steel, Units = 100 };

    private static string UnknownReason(SupplyPlan plan) => Assert.Single(plan.Unknown).Reason;

    [Fact]
    public void AnUnpricedYieldIsNamedAsItsGoodsRate()
    {
        var plan = Plan(Nutrition, Food("field", new YieldSupply.Unpriced(), new CandidateLead.Days(0), new CandidateWork.PerDay(10)));

        Assert.Equal("unknown: nutrition_per_day", UnknownReason(plan));
    }

    [Fact]
    public void UnpricedWorkIsNamedWithTheDistanceItLeavesUnpriced()
    {
        var plan = Plan(Nutrition, Food("field", new YieldSupply.Steady(5), new CandidateLead.Days(0), new CandidateWork.Unpriced()));

        Assert.Equal("unknown: work_per_day, path_distance", UnknownReason(plan));
    }

    [Fact]
    public void AnUnscheduledLeadIsNamed()
    {
        var plan = Plan(Nutrition, Food("field", new YieldSupply.Steady(5), new CandidateLead.Unscheduled(), new CandidateWork.PerDay(10)));

        Assert.Equal("unknown: lead_days", UnknownReason(plan));
    }

    [Fact]
    public void AnUnwalkedHaulIsNamed()
    {
        var plan = Plan(SteelDeficit, Haul("stack", 50, new YieldStorage.Accepts(100), new CandidatePath.Unwalked()));

        Assert.Equal("unknown: path_distance", UnknownReason(plan));
    }

    [Fact]
    public void AOneShotSourceWithAWalkIsFullyPriced()
    {
        var plan = Plan(SteelDeficit, Haul("stack", 50, new YieldStorage.Accepts(100), new CandidatePath.Walk(20)));

        Assert.Empty(plan.Unknown);
        Assert.Equal(50, Assert.Single(plan.Portfolio).Credit.Sum(c => c.Amount));
    }

    [Fact]
    public void AcceptedStorageBoundsAHauledYield()
    {
        var plan = Plan(SteelDeficit, Haul("stack", 50, new YieldStorage.Accepts(30), new CandidatePath.Walk(20)));

        Assert.Equal(30, Assert.Single(plan.Portfolio).Credit.Sum(c => c.Amount));
    }

    [Fact]
    public void UnreadStorageCreditsAHauledYieldNothingAndIsNotAFinding()
    {
        var plan = Plan(SteelDeficit, Haul("stack", 50, new YieldStorage.Unread(), new CandidatePath.Walk(20)));

        Assert.Empty(plan.Unknown);
        Assert.Equal(0, Assert.Single(plan.Portfolio).Credit.Sum(c => c.Amount));
    }

    [Fact]
    public void AHauledYieldThatStaysInPlaceIsADefect()
    {
        var row = Haul("stack", 50, new YieldStorage.InPlace(), new CandidatePath.Walk(20));

        Assert.Throws<SupplyPlanFactsException>(() => Plan(SteelDeficit, row));
    }

    [Fact]
    public void LeadStillRanksFlowCandidatesFirst()
    {
        var late = Food("late", new YieldSupply.Steady(10), new CandidateLead.Days(3), new CandidateWork.PerDay(1));
        var early = Food("early", new YieldSupply.Steady(10), new CandidateLead.Days(0), new CandidateWork.PerDay(100));

        var plan = Plan(Nutrition, late, early);

        Assert.Equal(["early", "late"], plan.Portfolio.Select(e => e.Candidate.Id));
        Assert.Equal(SupplyDecision.Open, plan.Portfolio[0].Decision);
    }

    [Fact]
    public void AMeasuredGroundRanksBeforeAnUnmeasuredOneAndTheNearerFirst()
    {
        SupplyCandidate Ground(string id, SourceNearness near) =>
            Food(id, new YieldSupply.Steady(2), new CandidateLead.Days(0), new CandidateWork.PerDay(10)) with { Kind = CandidateKind.Fishing, Nearness = near };

        var plan = Plan(Nutrition, Ground("a", new SourceNearness.Unmeasured()), Ground("b", new SourceNearness.Measured(400)), Ground("c", new SourceNearness.Measured(100)));

        Assert.Equal(["c", "b", "a"], plan.Portfolio.Select(e => e.Candidate.Id));
    }

    [Fact]
    public void ABoundedRateIsCappedByItsStockOverTheWindow()
    {
        var bounded = Food("hunt", new YieldSupply.Bounded(10, 20), new CandidateLead.Days(0), new CandidateWork.PerDay(1));

        var plan = Plan(Nutrition, bounded);

        // 20 nutrition over the 10-day window is 2 a day, under the 10 a day the rate offers.
        Assert.Equal(2, Assert.Single(plan.Portfolio).Credit.Sum(c => c.Amount), 9);
    }

    [Fact]
    public void TheYieldHelpersReadEachSupplyState()
    {
        Assert.True(CandidateRules.NutritionYield(new YieldSupply.Steady(3)).RateOf(out var steady));
        Assert.Equal(3, steady);
        Assert.False(CandidateRules.NutritionYield(new YieldSupply.Steady(3)).StockOf(out _));

        var bounded = CandidateRules.NutritionYield(new YieldSupply.Bounded(4, 12));
        Assert.True(bounded.RateOf(out var rate));
        Assert.True(bounded.StockOf(out var cap));
        Assert.Equal((4.0, 12L), (rate, cap));

        var stock = CandidateRules.NutritionYield(new YieldSupply.Stock(7));
        Assert.False(stock.RateOf(out _));
        Assert.True(stock.StockOf(out var count));
        Assert.Equal(7, count);

        var unpriced = CandidateRules.NutritionYield(new YieldSupply.Unpriced());
        Assert.False(unpriced.RateOf(out _));
        Assert.False(unpriced.StockOf(out _));
    }

    [Fact]
    public void ScalingARateKeepsItsStockAndLeavesAStockAlone()
    {
        Assert.Equal(new YieldSupply.Bounded(2, 12), new YieldSupply.Bounded(4, 12).ScaledRate(0.5));
        Assert.Equal(new YieldSupply.Steady(2), new YieldSupply.Steady(4).ScaledRate(0.5));
        Assert.Equal(new YieldSupply.Stock(7), new YieldSupply.Stock(7).ScaledRate(0.5));
        Assert.Equal(new YieldSupply.Unpriced(), new YieldSupply.Unpriced().ScaledRate(0.5));
    }

    [Fact]
    public void OnlyDailyWorkHasARate()
    {
        Assert.True(Food("a", new YieldSupply.Steady(1), new CandidateLead.Days(0), new CandidateWork.PerDay(6)).WorkRate(out var ticks));
        Assert.Equal(6, ticks);
        Assert.False(Food("b", new YieldSupply.Steady(1), new CandidateLead.Days(0), new CandidateWork.OneShot()).WorkRate(out _));
        Assert.False(Food("c", new YieldSupply.Steady(1), new CandidateLead.Days(0), new CandidateWork.Unpriced()).WorkRate(out _));
    }

    [Fact]
    public void FoodStateIsAlwaysRead()
    {
        Assert.Equal(CandidateState.Delivering, CandidateRules.FoodState(true, false));
        Assert.Equal(CandidateState.Designated, CandidateRules.FoodState(false, true));
        Assert.Equal(CandidateState.Closed, CandidateRules.FoodState(false, false));
    }

    [Fact]
    public void AFishingGroundWithNoFisherIsUnpricedAndWithoutResearchProgressUnscheduled()
    {
        var region = new FishingRegionReview
        {
            Observed = new()
            {
                Root = new(1, 2), Population = 400, MaxPopulation = 400, NutritionPerFish = 0.05,
                Reachable = true, Frozen = false, Zoned = false, NearestDistanceSquared = 25,
                Cells = [], Zones = [], YieldCurveValue = 1,
            },
            Reachable = true,
            Crew = new FishingCrew.NoFisher(),
        };
        var c = Assert.Single(FishingRules.FishingChannels(new FishingRequest { Regions = [region], Research = new FishingResearch.NoProgress() }));

        Assert.Equal(new YieldSupply.Unpriced(), c.Yields[0].Supply);
        Assert.Equal(new CandidateWork.Unpriced(), c.Work);
        Assert.Equal(new CandidateLead.Unscheduled(), c.Lead);
        Assert.Equal(new SourceNearness.Measured(25), c.Nearness);
        Assert.Equal(CandidateState.Closed, c.State);
    }
}
