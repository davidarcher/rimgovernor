using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The fishing rate and the seasonal harvest gap, the planner halves of the deleted food/fishing and farm/calendar
/// acceptance cases (#3405).
/// </summary>
public sealed class FoodChannelPolicyTests
{

    [Fact]
    public void IndependentAnimalProductsShareOneAnimalsFeed()
    {
        var milk = new RimGovernor.Brain.GameFacts.Observations.GatherableAnimal
        {
            PawnId = "animal", Race = "race", Fullness = 1, Resource = "milk", HandlerReachable = true,
            Yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing(3, 2, 1),
        };
        var eggs = new RimGovernor.Brain.GameFacts.Observations.EggLayerAnimal
        {
            PawnId = "animal", Race = "race", Yield = new RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing(2, 1, 2),
        };
        var gatherable = new AnimalProduct.Gatherable(milk) { Feed = new AnimalFeed.Plants(1) };
        var layer = new AnimalProduct.EggLayer(eggs) { Feed = new AnimalFeed.Plants(1) };
        Assert.Same(milk, gatherable.Observed);
        Assert.Same(eggs, layer.Observed);
        var channel = Assert.Single(FoodAnimalProductsRules.AnimalProductChannels([gatherable, layer]));
        Assert.True(channel.NutritionRate(out var rate));
        Assert.Equal(4, rate);
        Assert.Equal(1, Assert.Single(channel.Terms, term => term.Name == "productive_animals").Value);
        Assert.Equal(1, Assert.Single(channel.Terms, term => term.Name == "feed_per_day").Value);
        Assert.Equal(3, Assert.IsType<CandidateWork.PerDay>(channel.Work).Ticks);
    }
    private static FishingRegionReview Lake(double capacity) => new()
    {
        Observed = new()
        {
            Root = new(1, 2), Population = 400, MaxPopulation = 400, NutritionPerFish = 0.05,
            Reachable = true, Frozen = false, Zoned = false, NearestDistanceSquared = 0,
            Cells = [], Zones = [], YieldCurveValue = 1,
        },
        Crew = new FishingCrew.Fishers(2, 1000, capacity),
        Reachable = true,
        Delivering = false,
    };
    [Theory]
    [InlineData(5.0, 0.5)] // fishers outpace the lake: the regeneration bounds the rate (400 * 0.025 * 0.05)
    [InlineData(0.2, 0.2)] // the lake outpaces the fishers: their capacity bounds it
    public void FishingRateIsTheLesserOfRegenerationAndFisherCapacity(double capacity, double want)
    {
        var channel = Assert.Single(FishingRules.FishingChannels(new FishingRequest { Regions = [Lake(capacity)], Research = new FishingResearch.Researched() }));

        Assert.True(channel.NutritionRate(out var rate));
        Assert.Equal(want, rate, 9);
    }

    [Fact]
    public void AFisherWithoutSpeedIsNoCrewAndTheSlowestWorkingFisherSetsTheBatch()
    {
        Assert.IsType<FishingCrew.NoFisher>(FishingFactsRules.FishingCrewAt([new RimGovernor.Brain.GameFacts.Observations.FisherFacts { FishingYield = 1, FishingSpeed = 0 }], 2, 0.05, 1000));

        var crew = Assert.IsType<FishingCrew.Fishers>(FishingFactsRules.FishingCrewAt([new RimGovernor.Brain.GameFacts.Observations.FisherFacts { FishingYield = 1, FishingSpeed = 0 }, new RimGovernor.Brain.GameFacts.Observations.FisherFacts { FishingYield = 1.5, FishingSpeed = 2 }, new RimGovernor.Brain.GameFacts.Observations.FisherFacts { FishingYield = 1, FishingSpeed = 1 }], 2, 0.05, 1000));

        Assert.Equal((2.0, 1000.0), (crew.FishPerBatch, crew.WorkTicksPerBatch));
    }

    [Fact]
    public void UnresearchedFishingIsPendingOnlyWithAResearcher()
    {
        var pending = new RimGovernor.Brain.GameFacts.Observations.FishableWater { Regions = [], Fishers = [], BaseFishingDurationTicks = 1000, FishingResearched = false, ResearchBaseCost = 1000, ResearchProgress = 0, ResearchCostFactor = 1, ResearchPointsPerWorkTick = 0.01, ResearchDifficultySpeedFactor = 1, ResearcherSpeeds = [1] };

        Assert.IsType<FishingResearch.Researched>(FishingFactsRules.FishingResearchState(pending with { FishingResearched = true }));
        Assert.Equal(1000.0 / (0.01 * FishingFactsRules.FishingWorkTicksPerDay), Assert.IsType<FishingResearch.Pending>(FishingFactsRules.FishingResearchState(pending)).LeadDays, 9);
        Assert.IsType<FishingResearch.NoProgress>(FishingFactsRules.FishingResearchState(pending with { ResearcherSpeeds = [] }));
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(7.5)]
    public void HarvestGapDoesNotJumpAtTheFrost(double stretch)
    {
        var lastGrowing = new RimGovernor.Brain.GameFacts.Observations.FoodClimate
        {
            Season = "Fall", DayOfYear = 29, SowingNow = true,
            GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day == 0 || day > stretch)),
            GrowingDays = 30, GrowingDaysRemaining = 1, GrowingDaysUntil = 0, NonGrowingDays = stretch,
        };
        var firstFrost = lastGrowing with
        {
            DayOfYear = 30, GrowingDaysRemaining = 0, GrowingDaysUntil = stretch, SowingNow = false,
            GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day >= stretch)),
        };

        var before = CalendarRules.HarvestGapDays(lastGrowing, default(EquatableArray<DisasterCondition>));
        var after = CalendarRules.HarvestGapDays(firstFrost, default(EquatableArray<DisasterCondition>));

        Assert.True(before > 0);
        Assert.Equal(before, after, 9);
    }
}
