using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PowerMethodTests
{
    private static readonly PowerPlanning Planning = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { ["WoodFiredGenerator"] = PowerRules.ConstantPowerProfile },
    };

    private static PowerSite Site(string id, string definition, Cell cell, double watts, double output, bool powered) =>
        PowerBuildingFixtures.Trader(id, definition, cell, watts, output, powered);

    [Fact]
    public void FinitePendingDemandAboveTheFormerCeilingIsValid()
    {
        PowerRules.SelectPowerMethod(new PowerTopology(), new Bounds(20, 20), [], [], Planning with { PendingDemandW = 2e12 });
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidPendingDemandStillFails(double demand)
    {
        Assert.Throws<DomainException>(() => PowerRules.SelectPowerMethod(new PowerTopology(), new Bounds(20, 20), [], [],
            Planning with { PendingDemandW = demand }));
    }
    [Fact]
    public void TopologyReadsTheCanonicalBuildingServiceAndRatedCapacity()
    {
        var generator = Site("generator", "WoodFiredGenerator", new(1, 1), 1000, 0, false);
        var topology = new PowerTopology { Buildings = [generator] };
        Assert.Same(generator, Assert.Single(topology.Buildings));
        Assert.Equal(1000, topology.Buildings[0].BaseW);
        Assert.Equal(0, topology.Buildings[0].OutputW);
        Assert.False(topology.Buildings[0].Powered);
        Assert.Same(generator.Service.Fuel, topology.Buildings[0].Fuel);
    }

    // power/fuel's native half stages the generator and the refuel; this is the planner's half: an empty generator that
    // could carry the stove holds for ordinary refuelling instead of building another generator or conduit.
    [Fact]
    public void OutOfFuelProducerWithCapacityHoldsForRefuel()
    {
        var generator = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 0, powered: false);
        generator = generator with { Service = generator.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, OutOfFuel = true } } };
        var stove = Site("stove", "ElectricStove", new(8, 2), -350, 0, powered: false);
        var topology = new PowerTopology { Buildings = [generator, stove], Blackout = false, Eclipse = false };

        var proposal = PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), [], [], Planning);

        Assert.Equal(PowerMethod.WaitFuel, proposal.Method);
    }

    [Fact]
    public void GeneratorRankingRequiresTheCompleteNativeConstructionValue()
    {
        var option = new GeneratorOption { Definition = "InstalledGenerator", NominalW = 1700,
            Profile = PowerRules.ConstantPowerProfile, BuildValue = 136 };
        Assert.Equal(1700 * PowerRules.ConstantPowerProfile.Daily / 136, PowerRules.GeneratorYield(option), 6);
        Assert.Equal(double.PositiveInfinity, PowerRules.GeneratorYield(option with { BuildValue = 0 }));
        Assert.Throws<RequiredFactException>(() => PowerRules.GeneratorYield(option with { BuildValue = double.NaN }));
        Assert.Throws<RequiredFactException>(() => PowerRules.GeneratorYield(option with { BuildValue = -1 }));
        Assert.Throws<RequiredFactException>(() => PowerRules.GeneratorYield(option with { NominalW = 0 }));
    }
    // #3877: the fuel state is named: a not-refuelable building is never unfueled, a refuelable one is when out of fuel.
    [Fact]
    public void OnlyARefuelableBuildingOutOfFuelIsUnfueled()
    {
        var site = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 0, powered: false);

        Assert.False(site.Unfueled);
        Assert.False((site with { Service = site.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 5, TargetFuel = 30, OutOfFuel = false } } }).Unfueled);
        Assert.True((site with { Service = site.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, OutOfFuel = true } } }).Unfueled);
    }

    // #3877: a network with no consumer needs no power; a consumer the producers outrun leaves positive headroom.
    [Fact]
    public void PowerCoverageIsPlain()
    {
        var generator = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 1000, powered: true);
        var stove = Site("stove", "ElectricStove", new(8, 2), -350, 0, powered: true);

        Assert.Equal(new PowerCoverage(false, 0, false), PowerRules.PowerCoverageOf([generator]));
        var covered = PowerRules.PowerCoverageOf([generator, stove]);
        Assert.True(covered.Required);
        Assert.True(covered.HeadroomW > 0);
    }

    // #2799: a polar-night twelfth gives a solar generator nothing to deliver, so the wind turbine outranks it; an unread
    // daylight curve keeps the list order, and a lit twelfth leaves solar first.
    [Fact]
    public void WindOutranksSolarAtZeroDaylight()
    {
        GeneratorOption[] options =
        [
            new() { Definition = "SolarGenerator", Available = true, Profile = PowerRules.SolarPowerProfile, NominalW = 1700, BuildValue = 10 },
            new() { Definition = PowerRules.WindTurbineDefinition, Available = true, Profile = PowerRules.WindProfile(0.04, 2, 1), NominalW = 2300, BuildValue = 100 },
        ];
        var ranking = new GeneratorRanking { BatteryAvailable = true, DaylightHours = 12 };

        Assert.Equal(["SolarGenerator", PowerRules.WindTurbineDefinition], PowerRules.RankGenerators(options, ranking));
        Assert.Equal(["SolarGenerator", PowerRules.WindTurbineDefinition], PowerRules.RankGenerators(options, ranking with { DaylightHours = 12.0}));
        Assert.Equal([PowerRules.WindTurbineDefinition, "SolarGenerator"], PowerRules.RankGenerators(options, ranking with { DaylightHours = 0.0}));
    }
}
