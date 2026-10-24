using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Lifecycle;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The lifecycle and authority orders name every state (#3958): a save expects any tick or one tick, a temperature
/// bound is open or at a value, an observed generation is unclaimed or at a value, a scenario faction and arrival are
/// the base or a choice, and a scattered thing is roofed or not by its placement. Each round-trips through
/// <c>WireJsonContext</c> and keeps its case.
/// </summary>
public sealed class LifecycleOrderStatesTests
{
    [Fact]
    public void ExpectedSaveTickNamesAnyAndAtAndRoundTrips()
    {
        var any = WireJson.Deserialize<ExpectedSaveTick>(WireJson.Serialize<ExpectedSaveTick>(new ExpectedSaveTick.AnyCase()));
        var at = WireJson.Deserialize<ExpectedSaveTick>(WireJson.Serialize<ExpectedSaveTick>(new ExpectedSaveTick.AtCase(0)));

        Assert.IsType<ExpectedSaveTick.AnyCase>(any);
        Assert.Equal(0, Assert.IsType<ExpectedSaveTick.AtCase>(at).Value);
    }

    [Fact]
    public void TemperatureLimitNamesOpenAndAtAndRoundTrips()
    {
        var open = WireJson.Deserialize<TemperatureLimit>(WireJson.Serialize<TemperatureLimit>(new TemperatureLimit.OpenCase()));
        var at = WireJson.Deserialize<TemperatureLimit>(WireJson.Serialize<TemperatureLimit>(new TemperatureLimit.AtCase(-20f)));

        Assert.IsType<TemperatureLimit.OpenCase>(open);
        Assert.Equal(-20f, Assert.IsType<TemperatureLimit.AtCase>(at).Value);
    }

    [Fact]
    public void ObservedGenerationNamesUnclaimedAndAtAndAnswersIsAt()
    {
        var unclaimed = WireJson.Deserialize<ObservedGeneration>(WireJson.Serialize<ObservedGeneration>(new ObservedGeneration.UnclaimedCase()));
        var at = WireJson.Deserialize<ObservedGeneration>(WireJson.Serialize<ObservedGeneration>(new ObservedGeneration.AtCase(0)));

        Assert.IsType<ObservedGeneration.UnclaimedCase>(unclaimed);
        Assert.False(unclaimed.IsAt(0));
        Assert.True(at.IsAt(0));
        Assert.False(at.IsAt(1));
    }

    [Fact]
    public void ScenarioBaseMembersAreTheZeroOfEachEnum()
    {
        Assert.Equal(0, (int)ScenarioPlayerFaction.Base);
        Assert.Equal(0, (int)ScenarioArrivalMethod.Base);
        Assert.Equal(StartingThingPlacement.ScatteredRoofed, Enum.Parse<StartingThingPlacement>("ScatteredRoofed"));
    }

    [Fact]
    public void AWholeSpecKeepsItsBaseAndOpenStatesOnTheWire()
    {
        var spec = WireJson.Deserialize<NewColonySpec>(WireJson.Serialize(NewColonyFixtures.Spec()));

        Assert.Equal(ScenarioPlayerFaction.Base, spec.PlayerFaction);
        Assert.Equal(ScenarioArrivalMethod.Base, spec.ArrivalMethod);
        Assert.IsType<TemperatureLimit.OpenCase>(spec.MinTemperature);
        Assert.IsType<TemperatureLimit.OpenCase>(spec.MaxTemperature);
    }

    [Fact]
    public void AnUnclassifiedRefusalIsANamedClass()
    {
        Assert.Equal("Unclassified", RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified.ToString());
    }
}
