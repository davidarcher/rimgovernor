using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shelter and power families (#3257): every Need has a pure met, unmet and unknown predicate; housing's
/// preconditions replace the phase latch; the safe area is SeriousHarm and so also runs in the immediate pass.
/// </summary>
public sealed class ShelterFamilyTests
{
    private static readonly ShelterFacts Housed = new()
    {
        SleepingRecovered = Known(true),
        Colonists = 3L,
        IndoorCapacity = 3L + LayoutGraphRules.HousingSparePlaces,
        SleepingMin = Known(20.0),
        SleepingMax = Known(22.0),
        TemperatureOwed = Known(false),
        HeatDoorOwed = Known(false),
        SafeAreaOwed = Known(false),
        ColdExit = 10,
        HotExit = 30,
    };

    private static Need Of(ShelterFacts facts, NeedId id) =>
        Assert.Single(new ShelterFamily(true, true, true, true).Derive(facts), n => n.Id == id);

    [Fact]
    public void EveryComposedNeedIsDerivedAndMetForAHousedColony()
    {
        var needs = new ShelterFamily(true, true, true, true).Derive(Housed);
        Assert.Equal([ShelterFamily.SafeArea, ShelterFamily.Sleeping, ShelterFamily.Expansion, ShelterFamily.Temperature], needs.Select(n => n.Id));
        Assert.All(needs, n => Assert.Equal(Satisfaction.Met, n.Satisfaction));
        Assert.All(needs, n => Assert.Equal(Known(0.0), n.Deficit));
        Assert.Empty(new ShelterFamily(false, false, false, false).Derive(Housed));
        Assert.Equal([ShelterFamily.Temperature], new ShelterFamily(false, false, true, false).Derive(Housed).Select(n => n.Id));
    }

    [Fact]
    public void SleepingFollowsTheBedReviewAndWaitsOnNothing()
    {
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingRecovered = Known(false) }, ShelterFamily.Sleeping).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Of(Housed with { SleepingRecovered = Unknown<bool>() }, ShelterFamily.Sleeping).Satisfaction);
        Assert.Empty(Of(Housed, ShelterFamily.Sleeping).Preconditions);
    }

    [Fact]
    public void ExpansionKeepsOneSparePlaceAndWaitsOnSleeping()
    {
        var short1 = Of(Housed with { IndoorCapacity = 3L }, ShelterFamily.Expansion);
        Assert.Equal(Satisfaction.Unmet, short1.Satisfaction);
        Assert.True(short1.Deficit.TryGetValue(out var d) && d > 0 && d <= 1);
        Assert.Equal(Satisfaction.Unknown, Of(Housed with { Colonists = 0L }, ShelterFamily.Expansion).Satisfaction);
        Assert.Equal([ShelterFamily.Sleeping], Of(Housed, ShelterFamily.Expansion).Preconditions);
        Assert.Empty(Assert.Single(new ShelterFamily(false, true, false, false).Derive(Housed)).Preconditions);
        Assert.Equal(NeedTier.Development, Of(Housed, ShelterFamily.Expansion).Tier);
    }

    [Fact]
    public void PreconditionsHoldExpansionBehindUnmetSleeping()
    {
        var needs = new ShelterFamily(true, true, false, false).Derive(Housed with { SleepingRecovered = Known(false), IndoorCapacity = 0L });
        var standings = NeedsRules.NeedStandings(ShelterFamily.FamilyId, needs);
        Assert.Equal([NeedReadiness.Ready, NeedReadiness.Waiting], standings.Select(s => s.Readiness));
        Assert.Equal(ShelterFamily.Sleeping, standings[1].WaitingOn);
    }

    [Fact]
    public void TemperatureIsAPlainThresholdAtTheExitBars()
    {
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingMin = Known(9.9) }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Met, Of(Housed with { SleepingMin = Known(10.0) }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { SleepingMax = Known(30.1) }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { TemperatureOwed = Known(true) }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Of(Housed with { HeatDoorOwed = Known(true) }, ShelterFamily.Temperature).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Of(Housed with { SleepingMin = Unknown<double>() }, ShelterFamily.Temperature).Satisfaction);
    }

    [Fact]
    public void SafeAreaIsSeriousHarmAndRunsInTheImmediatePass()
    {
        var need = Of(Housed with { SafeAreaOwed = Known(true) }, ShelterFamily.SafeArea);
        Assert.Equal(NeedTier.SeriousHarm, need.Tier);
        Assert.Equal(Satisfaction.Unmet, need.Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Of(Housed with { SafeAreaOwed = Unknown<bool>() }, ShelterFamily.SafeArea).Satisfaction);
        Assert.NotEmpty(new ShelterFamily(false, false, false, true).SeriousHarmReads);
        Assert.Empty(new ShelterFamily(true, true, true, false).SeriousHarmReads);
        // Only the safe area is SeriousHarm: the immediate pass keeps it alone.
        Assert.Equal([ShelterFamily.SafeArea], new ShelterFamily(true, true, true, true).Derive(Housed).Where(n => n.Tier == NeedTier.SeriousHarm).Select(n => n.Id));
    }

    [Fact]
    public void SafeAreaMemoryOwesNothingKnownBeforeItsReview()
    {
        var memory = new SafeAreaMemory();
        Assert.False(memory.Owed("w").IsKnown);
        var reviewed = memory.Review("w", new ColonyProjection { Rooms = Known(new RoomObservation()) });
        Assert.True(reviewed.IsKnown);
        Assert.Equal(reviewed, memory.Owed("w"));
        Assert.False(memory.Owed("other").IsKnown);
        memory.Commit("w");
        Assert.Equal(Known(false), memory.Owed("w"));
        Assert.False(memory.Review("w", new ColonyProjection()).IsKnown);
    }

    [Fact]
    public void HousingNamesTheTwoHousingNeedsOnly()
    {
        Assert.True(ShelterFamily.Housing(ShelterFamily.Sleeping.FilingKey));
        Assert.True(ShelterFamily.Housing(ShelterFamily.Expansion.FilingKey));
        Assert.False(ShelterFamily.Housing(ShelterFamily.Temperature.FilingKey));
        Assert.False(ShelterFamily.Housing(ShelterFamily.SafeArea.FilingKey));
    }

    [Fact]
    public void PowerIsAPlainThresholdAndAFlareHoldsIt()
    {
        var family = new PowerFamily();
        Assert.Equal(Satisfaction.Met, Assert.Single(family.Derive(new PowerFacts { Standing = Known(true) })).Satisfaction);
        var unmet = Assert.Single(family.Derive(new PowerFacts { Standing = Known(false) }));
        Assert.Equal(Satisfaction.Unmet, unmet.Satisfaction);
        Assert.Equal(Known(1.0), unmet.Deficit);
        Assert.Equal(Satisfaction.Unknown, Assert.Single(family.Derive(new PowerFacts())).Satisfaction);
        Assert.Empty(family.SeriousHarmReads);
        Assert.Empty(family.Propose([unmet], new PowerFacts { Standing = Known(false) }));
        var held = Assert.Single(family.Propose([unmet], new PowerFacts { Standing = Known(false), OutageHeld = true }));
        Assert.Equal(PowerFamily.Basic, held.Need);
    }
}
