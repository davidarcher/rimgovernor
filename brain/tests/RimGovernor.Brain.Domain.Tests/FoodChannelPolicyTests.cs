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
        Population = Fact.Known(400.0),
        MaxPopulation = Fact.Known(400.0),
        NutritionPerFish = Fact.Known(0.05),
        FishPerBatch = Fact.Known(2.0),
        WorkTicksPerBatch = Fact.Known(1000.0),
        PawnFishWorkCapacity = Fact.Known(capacity),
        Reachable = Fact.Known(true),
        Frozen = Fact.Known(false),
        Designated = Fact.Known(false),
        Source = "fishing",
    };

    [Theory]
    [InlineData(5.0, 0.5)] // fishers outpace the lake: the regeneration bounds the rate (400 * 0.025 * 0.05)
    [InlineData(0.2, 0.2)] // the lake outpaces the fishers: their capacity bounds it
    public void FishingRateIsTheLesserOfRegenerationAndFisherCapacity(double capacity, double want)
    {
        var channel = Assert.Single(FishingRules.FishingChannels(new FishingRequest { Regions = [Lake(capacity)], Researched = Fact.Known(true) }));

        Assert.Equal(want, channel.Yields[0].PerDay.ValueOrDefault, 9);
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
