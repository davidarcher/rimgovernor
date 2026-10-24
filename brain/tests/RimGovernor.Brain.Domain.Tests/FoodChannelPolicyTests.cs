using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The fishing rate and the seasonal harvest gap, the planner halves of the deleted food/fishing and farm/calendar
/// acceptance cases (#3405).
/// </summary>
public sealed class FoodChannelPolicyTests
{
    private static readonly Fact<EquatableArray<DisasterCondition>> NoConditions = Fact.Unknown<EquatableArray<DisasterCondition>>();

    private static FishingRegion Lake(double capacity) => new()
    {
        Id = "lake",
        Population = 400,
        MaxPopulation = 400,
        NutritionPerFish = 0.05,
        Crew = new FishingCrew.Fishers(2, 1000, capacity),
        Reachable = true,
        Frozen = false,
        Designated = false,
        Source = "fishing",
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
        Assert.IsType<FishingCrew.NoFisher>(FishingFactsRules.FishingCrewAt([new Fisher(1, 0)], 2, 0.05, 1000));

        var crew = Assert.IsType<FishingCrew.Fishers>(FishingFactsRules.FishingCrewAt([new Fisher(1, 0), new Fisher(1.5, 2), new Fisher(1, 1)], 2, 0.05, 1000));

        Assert.Equal((2.0, 1000.0), (crew.FishPerBatch, crew.WorkTicksPerBatch));
    }

    [Fact]
    public void UnresearchedFishingIsPendingOnlyWithAResearcher()
    {
        var pending = new FishingResearchInputs { BaseCost = 1000, Progress = 0, CostFactor = 1, PointsPerWorkTick = 0.01, Difficulty = 1, ResearcherSpeeds = [1] };

        Assert.IsType<FishingResearch.Researched>(FishingFactsRules.FishingResearchState(pending with { Researched = true }));
        Assert.Equal(1000.0 / (0.01 * FishingFactsRules.FishingWorkTicksPerDay), Assert.IsType<FishingResearch.Pending>(FishingFactsRules.FishingResearchState(pending)).LeadDays, 9);
        Assert.IsType<FishingResearch.NoProgress>(FishingFactsRules.FishingResearchState(pending with { ResearcherSpeeds = [] }));
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(7.5)]
    public void HarvestGapDoesNotJumpAtTheFrost(double stretch)
    {
        var lastGrowing = new Calendar { GrowingDays = 30, GrowingDaysRemaining = 1, GrowingDaysUntil = 0, NonGrowingDays = stretch };
        var firstFrost = lastGrowing with { GrowingDaysRemaining = 0, GrowingDaysUntil = stretch };

        var before = CalendarRules.HarvestGapDays(Fact.Known(lastGrowing), NoConditions).ValueOrDefault;
        var after = CalendarRules.HarvestGapDays(Fact.Known(firstFrost), NoConditions).ValueOrDefault;

        Assert.True(before > 0);
        Assert.Equal(before, after, 9);
    }
}
