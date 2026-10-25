using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts.Lifecycle;
using RimGovernor.Brain.GameOrders.Lifecycle;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The lifecycle and authority orders name every state (#3958): a save expects any tick or one tick, a temperature
/// bound is open or at a value, an observed generation is unclaimed or at a value, a scenario faction and arrival are
/// the base or a choice, and a scattered thing is roofed or not by its placement. Each round-trips through
/// <c>OrdersJsonContext</c> and keeps its case.
/// </summary>
public sealed class LifecycleOrderStatesTests
{
    [Fact]
    public void ExpectedSaveTickNamesAnyAndAtAndRoundTrips()
    {
        var any = OrdersJson.Deserialize<ExpectedSaveTick>(OrdersJson.Serialize<ExpectedSaveTick>(new ExpectedSaveTick.AnyCase()));
        var at = OrdersJson.Deserialize<ExpectedSaveTick>(OrdersJson.Serialize<ExpectedSaveTick>(new ExpectedSaveTick.AtCase(0)));

        Assert.IsType<ExpectedSaveTick.AnyCase>(any);
        Assert.Equal(0, Assert.IsType<ExpectedSaveTick.AtCase>(at).Value);
    }

    [Fact]
    public void TemperatureLimitNamesOpenAndAtAndRoundTrips()
    {
        var open = OrdersJson.Deserialize<TemperatureLimit>(OrdersJson.Serialize<TemperatureLimit>(new TemperatureLimit.OpenCase()));
        var at = OrdersJson.Deserialize<TemperatureLimit>(OrdersJson.Serialize<TemperatureLimit>(new TemperatureLimit.AtCase(-20f)));

        Assert.IsType<TemperatureLimit.OpenCase>(open);
        Assert.Equal(-20f, Assert.IsType<TemperatureLimit.AtCase>(at).Value);
    }

    [Fact]
    public void ObservedGenerationNamesUnclaimedAndAtAndAnswersIsAt()
    {
        var unclaimed = OrdersJson.Deserialize<ObservedGeneration>(OrdersJson.Serialize<ObservedGeneration>(new ObservedGeneration.UnclaimedCase()));
        var at = OrdersJson.Deserialize<ObservedGeneration>(OrdersJson.Serialize<ObservedGeneration>(new ObservedGeneration.AtCase(0)));

        Assert.IsType<ObservedGeneration.UnclaimedCase>(unclaimed);
        Assert.False(unclaimed.IsAt(0));
        Assert.True(at.IsAt(0));
        Assert.False(at.IsAt(1));
    }

    [Fact]
    public void ScenarioBaseIsANamedMemberAndZeroIsNoState()
    {
        Assert.DoesNotContain(0, Enum.GetValues<ScenarioPlayerFaction>().Select(v => (int)v));
        Assert.DoesNotContain(0, Enum.GetValues<ScenarioArrivalMethod>().Select(v => (int)v));
        Assert.Equal(ScenarioPlayerFaction.Base, OrdersJson.Deserialize<ScenarioPlayerFaction>(OrdersJson.Serialize(ScenarioPlayerFaction.Base)));
        Assert.Equal(ScenarioArrivalMethod.Base, OrdersJson.Deserialize<ScenarioArrivalMethod>(OrdersJson.Serialize(ScenarioArrivalMethod.Base)));
        Assert.Equal(StartingThingPlacement.ScatteredRoofed, Enum.Parse<StartingThingPlacement>("ScatteredRoofed"));
    }

    [Fact]
    public void AWholeSpecKeepsItsBaseAndOpenStatesOnTheWire()
    {
        var spec = OrdersJson.Deserialize<NewColonySpec>(OrdersJson.Serialize(NewColonyFixtures.Spec()));

        Assert.Equal(ScenarioPlayerFaction.Base, spec.PlayerFaction);
        Assert.Equal(ScenarioArrivalMethod.Base, spec.ArrivalMethod);
        Assert.IsType<TemperatureLimit.OpenCase>(spec.MinTemperature);
        Assert.IsType<TemperatureLimit.OpenCase>(spec.MaxTemperature);
    }

    [Fact]
    public void AnUnclassifiedRefusalIsANamedClass()
    {
        Assert.Equal("Unclassified", RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified.ToString());
    }
}
