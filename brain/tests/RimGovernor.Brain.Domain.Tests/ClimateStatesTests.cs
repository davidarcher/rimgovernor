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

    internal static TemperatureCooling Cooling(
        ClimatePower? power = null,
        IReadOnlyList<HeatCampfire>? campfires = null,
        IReadOnlyList<SleepingPerson>? sleepers = null) => new()
    {
        Candidates = [Thermal("fuel-heat", 21), Thermal("fuel-cool", -11)],
        Definitions = [Definition("fuel-heat"), Definition("fuel-cool")], Buildings = [],
        Builders = [PawnFixtures.Worker("builder") with { Work = PawnFixtures.WorkTable(true, [new() { Observed = new() { DefName = "Construction", Priority = 1, Disabled = false } }]) }],
        Stock = new Dictionary<Resource, long> { [new("fuel")] = 20 }, RenewableFuels = new HashSet<Resource>(),
        Power = power ?? new ClimatePower.NoCensus(),
        Cells = [],
        HeatCampfires = campfires ?? [],
        Sleepers = sleepers ?? [],
    };

    internal static TemperatureDefinition Thermal(string name, double heat, bool controlled = false) => new()
    {
        Name = name, HeatPerSecond = heat, PowerW = controlled ? 175 : 0, MinimumC = -100, MaximumC = 100,
        Wall = false, Controlled = controlled, BuildValue = 10, Fuels = controlled ? [] : [new("fuel")],
    };

    internal static PlanningDefinition Definition(string name) => new()
    {
        Name = name, Available = true, FixedConstruction = new() { Costs = [] },
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
        var burning = HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.Switch { Allowed = true });
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
        Assert.Null(HeatCampfire.Of("fire", default, new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.Switch { Allowed = true }));
        Assert.Null(HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch()));
        Assert.NotNull(HeatCampfire.Of("fire", "room", new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.Switch { Allowed = false }));
    }

    [Fact]
    public void NoRoomsOweNoTemperatureWork()
    {
        var sleeping = new SleepingObservation();
        Assert.False(TemperatureMethodRules.TemperatureOwed(RoomTestFacts.NoRooms, sleeping, []));
        Assert.False(TemperatureMethodRules.TemperatureOwed(Rooms(21, true, "bed"), sleeping, []));
    }

    [Fact]
    public void ThePowerCensusIsTheTopology()
    {
        var census = Assert.IsType<ClimatePower.Census>(ClimatePower.Of(new PowerTopology()));
        Assert.Empty(census.Topology.Buildings);
    }

    [Fact]
    public void FuelHeatMustReachTheSleeperBandAndRenewableFuelKeepsItsPreference()
    {
        var burner = Thermal("burner", 21) with { MaximumC = 28 };
        var heater = Thermal("thermostat", 21, true);
        var power = new ClimatePower.Census(new() { Buildings = [PowerBuildingFixtures.Trader("source", "source", new(9, 9), 1000, 1000, true)] });
        var cooling = Cooling(power) with { Candidates = [heater, burner], Definitions = [Definition(heater.Name), Definition(burner.Name)] };
        Assert.Equal(heater.Name, cooling.Options(true, 16)[0].Name);
        cooling = cooling with { RenewableFuels = new HashSet<Resource> { new("fuel") } };
        Assert.Equal(burner.Name, cooling.Options(true, 16)[0].Name);
        Assert.Equal(heater.Name, Assert.Single(cooling.Options(true, 30)).Name);
        var weak = burner with { Name = "weak", HeatPerSecond = 1, BuildValue = 5 };
        var compared = cooling with { Candidates = [weak, burner], Definitions = [Definition(weak.Name), Definition(burner.Name)] };
        Assert.Equal(burner.Name, compared.Options(true, 16)[0].Name);
        Assert.Throws<RequiredFactException>(() => (cooling with { Definitions = [] }).Options(true, 16));
        Assert.Throws<RequiredFactException>(() => (heater with { PowerW = double.NaN }).Validate());
    }

    [Fact]
    public void AStandingThermostatOutsideTheSleeperBandNeedsARealSettingChange()
    {
        var sleeper = new SleepingPerson { Native = SleepingNativeFixtures.Person(id: new PawnId("p")),
            Pawn = SleepingNativeFixtures.Pawn(id: new PawnId("p"), ownedBed: "bed", comfortableMin: 26, comfortableMax: 35) };
        var heater = Thermal("installed", 21, true);
        var native = PowerBuildingFixtures.Trader("thermostat", heater.Name, Inside, -175, -175, true);
        native = native with { Settings = native.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Temperature(new(), 21) } };
        var cooling = Cooling(sleepers: [sleeper]) with { Candidates = [heater], Definitions = [Definition(heater.Name)], Buildings = [new() { Observed = native }] };
        var proposal = TemperatureMethodRules.SelectTemperatureMethod(Rooms(23, true, "bed"), ["bed"], cooling, Limits);
        Assert.Equal(TemperatureMethod.SetTarget, proposal.Method);
        Assert.Equal("thermostat", proposal.Thing);
        Assert.Equal(26, proposal.TargetC);
        var action = DomainAction.NewBuildingTemperatureAction(new("target"), new(proposal.Thing, proposal.TargetC));
        Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingPatchCase>(RimGovernor.Brain.GameAccess.IntentActionRules.IntentAction("target", action).Intent);
        var missing = native with { Settings = native.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() } };
        Assert.Throws<RequiredFactException>(() => TemperatureMethodRules.SelectTemperatureMethod(Rooms(23, true, "bed"), ["bed"],
            cooling with { Buildings = [new() { Observed = missing }] }, Limits));
    }

    [Fact]
    public void ADiscoveredWallCoolerServesOnlyTheRoomBehindItsActualFacing()
    {
        var cooler = Thermal("installed-cooler", -21, true) with { Wall = true };
        var native = PowerBuildingFixtures.Trader("cooler", cooler.Name, new(1, 2), -200, -200, true);
        var cooling = Cooling() with { Candidates = [cooler], Buildings = [new() { Observed = native }] };
        Assert.Single(cooling.Existing([Inside], false));
        Assert.Empty(cooling.Existing([new(1, 3)], false));
    }

    [Fact]
    public void AnUnusuallyCoolSleeperKeepsCoolingOwedBelowTheOrdinaryHotThreshold()
    {
        var sleeper = new SleepingPerson { Native = SleepingNativeFixtures.Person(id: new PawnId("p")),
            Pawn = SleepingNativeFixtures.Pawn(id: new PawnId("p"), ownedBed: "bed", comfortableMin: -10, comfortableMax: 10) };
        var sleeping = new SleepingObservation { People = [sleeper] };
        Assert.True(TemperatureMethodRules.TemperatureOwed(Rooms(15, true, "bed"), sleeping, []));
        Assert.Equal(TemperatureMethod.Cool, MethodFor(Rooms(15, true, "bed"), ["bed"], Cooling(sleepers: [sleeper])));
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
