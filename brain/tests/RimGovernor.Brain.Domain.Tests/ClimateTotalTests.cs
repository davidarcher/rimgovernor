using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClimateTotalTests
{
    private static Tables EmptyTables(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot snapshot) =>
        new(default, default, default, []) { PawnCensus = new() { Context = snapshot.Context, Pawns = [] } };

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(13)]
    public void AnIncompleteSeasonalCurveFailsTheWholeColonyRead(int count)
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        var malformed = snapshot with
        {
            Gear = snapshot.Gear with { Climate = snapshot.Gear.Climate with { Temperatures = [.. Enumerable.Repeat(18.0, count)] } },
        };
        var error = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(malformed, new ObservationIdentity { Context = snapshot.Context }, EmptyTables(snapshot), null));
        Assert.Contains("gear climate must contain twelve finite seasonal temperatures", error.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonfiniteSeasonCannotBecomeATemperateMap(double invalid)
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        double[] curve = [.. Enumerable.Repeat(18.0, 12)];
        curve[7] = invalid;
        var malformed = snapshot with { Gear = snapshot.Gear with { Climate = snapshot.Gear.Climate with { Temperatures = EquatableArray.From(curve) } } };
        var error = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(malformed, new ObservationIdentity { Context = snapshot.Context }, EmptyTables(snapshot), null));
        Assert.Contains("gear climate must contain twelve finite seasonal temperatures", error.Message);
    }

    [Fact]
    public void AMissingGearClimateFailsTheWholeColonyRead()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        var missingGear = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(snapshot with { Gear = null! }, new ObservationIdentity { Context = snapshot.Context }, EmptyTables(snapshot), null));
        Assert.Contains("missing total gear census", missingGear.Message);
        var missingClimate = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(snapshot with { Gear = snapshot.Gear with { Climate = null! } }, new ObservationIdentity { Context = snapshot.Context }, EmptyTables(snapshot), null));
        Assert.Contains("missing total gear climate", missingClimate.Message);
    }

    [Theory]
    [InlineData(-20.0, true, false)]
    [InlineData(18.0, false, false)]
    [InlineData(50.0, false, true)]
    public void TheObservedSeasonalMeansDetermineBothMapLatchesAndTheRoof(double temperature, bool cold, bool hot)
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        var read = snapshot with { Gear = snapshot.Gear with { Climate = snapshot.Gear.Climate with { Temperatures = [.. Enumerable.Repeat(temperature, 12)] } } };
        var reading = ColonyFactsCoreTests.Reading() with
        {
            Facts = (ColonyFactsCoreTests.Reading()).Facts with { Census = read }
        };
        var curve = ClimateRules.RequireOutdoorCurve(reading.Snapshot.Gear.Climate.Temperatures);
        Assert.Equal(cold, LayoutRoofRules.ColdMapCurve(curve));
        Assert.Equal(hot, LayoutRoofRules.HotMapCurve(curve));
        Assert.Equal(read.Gear.Climate.Temperatures, curve);

        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(1, 1, 3, 3)) };
        var roof = LayoutPlan.PlanRoofed(new LayoutPlan { Rooms = [room] }, new RoofInput { Curve = curve });
        Assert.Equal(!(cold || hot), roof.Roofed.Empty());
        if (cold || hot)
        {
            Assert.Equal(room.Region.Area(), roof.Roofed.Area());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ALatchedClimateDoesNotHideAMissingRequiredCurve(bool cold, bool hot)
    {
        var plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(1, 1, 3, 3)) }] };
        var error = Assert.Throws<RequiredFactException>(() => LayoutPlan.PlanRoofed(plan, new RoofInput { Cold = cold, Hot = hot }));
        Assert.Equal("outdoor_curve", error.Fact);
    }

    [Fact]
    public void AGroupWithNoColdRequirementHasNoCoolingRelationship()
    {
        Assert.Null(ClimateRules.GroupClimateOf(new StationFacts(), new RoomGroup()));
    }
}
