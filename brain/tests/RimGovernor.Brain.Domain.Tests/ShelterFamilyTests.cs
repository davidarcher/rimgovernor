using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shelter and power families (#3257): every Need has a pure met, unmet and unknown predicate; the plan houses
/// the colonists exactly, with no spare place (#3749); the safe area is SeriousHarm and so also runs in the immediate pass.
/// </summary>
public sealed class ShelterFamilyTests
{
    private static readonly ShelterFacts Housed = new()
    {
        SleepingRecovered = true,
        SleepingMin = 20.0,
        SleepingMax = 22.0,
        TemperatureOwed = false,
        HeatDoorOwed = Known(false),
        SafeAreaOwed = Known(false),
        ColdExit = 10,
        HotExit = 30,
    };

    private static Need Of(ShelterFacts facts, NeedId id) =>
        Assert.Single(new ShelterFamily(true, true, true).Derive(facts), n => n.Id == id);

    [Fact]
    public void EveryComposedNeedIsDerivedAndMetForAHousedColony()
    {
        var needs = new ShelterFamily(true, true, true).Derive(Housed);
        Assert.Equal([ShelterFamily.SafeArea, ShelterFamily.Sleeping, ShelterFamily.Temperature], needs.Select(n => n.Id));
        Assert.All(needs, n => Assert.Equal(Satisfaction.Met, n.Satisfaction));
        Assert.All(needs, n => Assert.Equal(0.0, n.Deficit));
        Assert.Empty(new ShelterFamily(false, false, false).Derive(Housed));
        Assert.Equal([ShelterFamily.Temperature], new ShelterFamily(false, true, false).Derive(Housed).Select(n => n.Id));
    }

    [Fact]
    public void SleepingFollowsTheBedReviewAndWaitsOnNothing()
    {
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingRecovered = false }, ShelterFamily.Sleeping).Satisfaction);
        Assert.Empty(Of(Housed, ShelterFamily.Sleeping).Preconditions);
    }

    [Fact]
    public void AColonyOfThreeSleepsInThreePlacesWithNoSpare()
    {
        // The station graph's sleep nodes count the colonists exactly (#3749): three colonists, three sleep slots.
        var input = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild").GraphInput();
        Assert.Equal(3, input.Population.Colonists);
        Assert.Equal(3, StationGraph.SleepGraph(input).Nodes.Where(n => n.Purpose == StationPurpose.Sleep).Sum(n => n.Count));

        // The shelter target is the colonist count: three places for three colonists owe nothing, two owe one.
        var housed = new RoundsFacts { Colonists = 3L, BedCapacity = 3L, IndoorCapacity = 3L };
        Assert.Equal(0, ShelterRules.StandInsOwed(housed).Missing);
        Assert.Equal((3L, 1), ShelterRules.StandInsOwed(housed with { BedCapacity = 2L, IndoorCapacity = 2L }));
    }

    [Fact]
    public void TemperatureIsAPlainThresholdAtTheExitBars()
    {
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingMin = 9.9 }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Met, Of(Housed with { SleepingMin = 10.0 }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingMax = 30.1 }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { TemperatureOwed = true }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { HeatDoorOwed = Known(true) }, ShelterFamily.Temperature).Satisfaction);
    }

    [Fact]
    public void SafeAreaIsSeriousHarmAndRunsInTheImmediatePass()
    {
        var need = Of(Housed with { SafeAreaOwed = Known(true) }, ShelterFamily.SafeArea);
        Assert.Equal(NeedTier.SeriousHarm, need.Tier);
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal("safe area edits", Assert.Throws<RequiredFactException>(() => Of(Housed with { SafeAreaOwed = Unknown<bool>() }, ShelterFamily.SafeArea)).Fact);
        Assert.NotEmpty(new ShelterFamily(false, false, true).SeriousHarmReads);
        Assert.Empty(new ShelterFamily(true, true, false).SeriousHarmReads);
        // Only the safe area is SeriousHarm: the immediate pass keeps it alone.
        Assert.Equal([ShelterFamily.SafeArea], new ShelterFamily(true, true, true).Derive(Housed).Where(n => n.Tier == NeedTier.SeriousHarm).Select(n => n.Id));
    }

    [Fact]
    public void SafeAreaMemoryOwesNothingKnownBeforeItsReview()
    {
        var memory = new SafeAreaMemory();
        Assert.False(memory.Owed("w").IsKnown);
        var reviewed = memory.Review("w", ColonyFactsCoreTests.Projection() with { Rooms = Known(RoomObservation.NoRooms) });
        Assert.True(reviewed.IsKnown);
        Assert.Equal(reviewed, memory.Owed("w"));
        Assert.False(memory.Owed("other").IsKnown);
        memory.Commit("w");
        Assert.Equal(Known(false), memory.Owed("w"));
        Assert.False(memory.Review("w", ColonyFactsCoreTests.Projection()).IsKnown);
    }

    [Fact]
    public void PowerIsAPlainThresholdAndAFlareHoldsIt()
    {
        var family = new PowerFamily();
        Assert.Equal(Satisfaction.Met, Assert.Single(family.Derive(new PowerFacts { Standing = Known(true) })).Satisfaction);
        var unmet = Assert.Single(family.Derive(new PowerFacts { Standing = Known(false) }));
        Assert.Equal(Satisfaction.Unmet, unmet.Satisfaction);
        Assert.Equal(1.0, unmet.Deficit);
        Assert.Equal("power standing", Assert.Throws<RequiredFactException>(() => Assert.Single(family.Derive(new PowerFacts()))).Fact);
        Assert.Empty(family.SeriousHarmReads);
        Assert.Empty(family.Propose([unmet], new PowerFacts { Standing = Known(false) }));
        var held = Assert.Single(family.Propose([unmet], new PowerFacts { Standing = Known(false), OutageHeld = true }));
        Assert.Equal(PowerFamily.Basic, held.Need);
    }
}
