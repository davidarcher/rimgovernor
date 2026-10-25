using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3956: the temperature, outdoor hazard, blight and pest reads are plain values, named states or a
/// <see cref="RequiredFactException"/>; none reads an unread input as a default.
/// </summary>
public sealed class ClimateStatesTests
{
    private static readonly PlannerSettings Limits = Rules.DefaultPlannerSettings();

    private static readonly Cell Inside = new(1, 1);

    private static TemperatureCooling Cooling(
        ClimateGear? cooler = null,
        ClimatePower? power = null,
        IReadOnlyList<HeatCampfire>? campfires = null,
        IReadOnlyList<SleepingPerson>? sleepers = null) => new()
    {
        Cooler = cooler ?? new ClimateGear.NotPlaceable(),
        Power = power ?? new ClimatePower.NoCensus(),
        Cells = [],
        HeatCampfires = campfires ?? [],
        Sleepers = sleepers ?? [],
        Heater = "",
        HeaterGear = new ClimateGear.NotPlaceable(),
        HeatFuelFinite = false,
    };

    private static RoomObservation Rooms(double temperature, bool enclosed = true, params string[] beds) =>
        RoomFixtures.Census((RoomFixtures.State("room", beds: beds, temperatureC: temperature, enclosed: enclosed), [Inside]));

    private static TemperatureMethod MethodFor(RoomObservation rooms, string[] eligible, TemperatureCooling? cooling = null) =>
        TemperatureMethodRules.SelectTemperatureMethod(rooms, [.. eligible], cooling ?? Cooling(), Limits).Method;

    [Fact]
    public void NoEligibleBedNeedsAShelter() =>
        Assert.Equal(TemperatureMethod.ShelterNeeded, MethodFor(Rooms(21, true, "bed"), []));

    [Fact]
    public void AnEligibleBedInNoRoomIsNamedNotAssumedComfortable() =>
        Assert.Equal(TemperatureMethod.BedsUnroomed, MethodFor(Rooms(21, true, "bed"), ["bed", "other"]));

    [Fact]
    public void ACoolEnclosedRoomHasNoDeficitAndAnOpenOneWaits()
    {
        Assert.Equal(TemperatureMethod.NoMethod, MethodFor(Rooms(21, true, "bed"), ["bed"]));
        Assert.Equal(TemperatureMethod.Wait, MethodFor(Rooms(21, false, "bed"), ["bed"]));
    }

    [Fact]
    public void AColdRoomHeatsAndAHotRoomCools()
    {
        var heat = TemperatureMethodRules.SelectTemperatureMethod(Rooms(2, true, "bed"), ["bed"], Cooling(), Limits);
        Assert.Equal(TemperatureMethod.Heat, heat.Method);
        Assert.NotEqual(default, heat.Key);
        Assert.Equal(TemperatureMethod.Cool, MethodFor(Rooms(40, true, "bed"), ["bed"]));
    }

    [Fact]
    public void ARefuelSwitchOutranksTheRoomMethodAndNamesTheCampfire()
    {
        var sleeper = new SleepingPerson { Native = SleepingNativeFixtures.Person(id: new PawnId("p")), Pawn = SleepingNativeFixtures.Pawn(id: new PawnId("p"), ownedBed: "bed", comfortableMin: 16, comfortableMax: 26) };
        var burning = HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.BenchAutoRefuel.Refuelable { Allowed = true });
        var cooling = Cooling(campfires: [burning!], sleepers: [sleeper]);
        var off = TemperatureMethodRules.SelectTemperatureMethod(Rooms(30, true, "bed"), ["bed"], cooling, Limits);
        Assert.Equal(TemperatureMethod.RefuelOff, off.Method);
        Assert.Equal("fire", off.Thing);
        Assert.NotNull(TemperatureMethodRules.CampfireRefuel(Rooms(30, true, "bed"), cooling.HeatCampfires, cooling.Sleepers));
        Assert.Null(TemperatureMethodRules.CampfireRefuel(Rooms(21, true, "bed"), cooling.HeatCampfires, cooling.Sleepers));
    }

    [Fact]
    public void ACampfireWithAnUnreadRoomOrToggleIsNotBuilt()
    {
        Assert.Null(HeatCampfire.Of("fire", default, new RimGovernor.Brain.GameFacts.Observations.BenchAutoRefuel.Refuelable { Allowed = true }));
        Assert.Null(HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.BenchAutoRefuel.NotRefuelable()));
        Assert.NotNull(HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.BenchAutoRefuel.Refuelable { Allowed = false }));
    }

    [Fact]
    public void NoRoomsOweNoTemperatureWork()
    {
        var sleeping = new SleepingObservation();
        Assert.False(TemperatureMethodRules.TemperatureOwed(RoomTestFacts.NoRooms, sleeping, []));
        Assert.False(TemperatureMethodRules.TemperatureOwed(Rooms(21, true, "bed"), sleeping, []));
    }

    [Fact]
    public void AClimateGearIsPlaceableOnlyWhenAvailableAndDrawn()
    {
        Assert.IsType<ClimateGear.NotPlaceable>(ClimateGear.Of("Cooler", false, 0));
        Assert.Equal(new ClimateGear.Placeable(200), ClimateGear.Of("Cooler", true, 200.0));
        Assert.Throws<RequiredFactException>(() => ClimateGear.Of("Cooler", true, double.NaN));
    }

    [Fact]
    public void ThePowerCensusIsTheTopology()
    {
        var census = Assert.IsType<ClimatePower.Census>(ClimatePower.Of(new PowerTopology()));
        Assert.Empty(census.Topology.Buildings);
    }

    [Fact]
    public void OutdoorHazardsDeriveFromTheConditionAndTemperatureReads()
    {
        var calm = new ReviewFacts { DisasterConditions = [], Census = ColonyFactsCoreTests.Snapshot() with { OutdoorTemperatureC = 15.0 } };
        Assert.False(OutdoorHazardRules.OutdoorAcquisitionHeld(calm));
        var cold = calm with { Census = calm.Census with { OutdoorTemperatureC = -25.0 } };
        Assert.Equal([new OutdoorHazard(HazardKind.ExtremeCold)], OutdoorHazardRules.OutdoorHazardsOf(cold).ToList());
        Assert.False(OutdoorHazardRules.OutdoorHazardHeldDash(OutdoorHazardRules.OutdoorHazardsOf(cold), true));
        Assert.True(OutdoorHazardRules.OutdoorHazardHeldDash(OutdoorHazardRules.OutdoorHazardsOf(cold), false));
        var hot = calm with { Census = calm.Census with { OutdoorTemperatureC = 50.0 } };
        Assert.True(OutdoorHazardRules.OutdoorHazardHeldDash(OutdoorHazardRules.OutdoorHazardsOf(hot), true));

    }

    [Fact]
    public void BlightIsADeficitWhileAnyPlantStands()
    {
        Assert.False(BlightRules.BlightDeficit([]));
        Assert.True(BlightRules.BlightDeficit([new BlightedPlant { Observed = new() { Thing = new() { Id = "plant" }, Designated = false }, Entity = new() { Id = "plant", DefName = "Plant_Rice", Label = "rice", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(1, 2)) } }]));
    }

    [Fact]
    public void AnUnratedWallIsNotAZeroStrengthWall()
    {
        Assert.NotEqual<WallStrength>(new WallStrength.Unrated(), new WallStrength.Rated(0));
        Assert.Equal(new PerimeterWall(new WallStrength.Rated(300), new WallStrength.Unrated()).HP, new WallStrength.Rated(300));
    }
}
