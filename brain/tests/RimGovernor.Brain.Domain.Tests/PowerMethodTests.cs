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

    // The planner's half of restoration (task 5.3): an empty generator that could carry the stove counts toward capacity
    // only while the power Need's restoration choice restores it; one it cannot restore is not there, and generation answers.
    [Fact]
    public void OutOfFuelProducerWithCapacityRestoresOnlyWhileTheChoiceRestoresIt()
    {
        var generator = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 0, powered: false);
        generator = generator with { Service = generator.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, AutoRefuelPercent = 0.3, OutOfFuel = true } } };
        var stove = Site("stove", "ElectricStove", new(8, 2), -350, 0, powered: false);
        var topology = new PowerTopology { Buildings = [generator, stove], Blackout = false, Eclipse = false };

        var restoring = PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), [], [], Planning with { Restorable = new HashSet<string>(["gen"], StringComparer.Ordinal) });
        Assert.Equal(PowerMethod.Restore, restoring.Method);

        var dead = PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), [], [], Planning);
        Assert.NotEqual(PowerMethod.Restore, dead.Method);
    }

    // #3877: the fuel state is named: a not-refuelable building is never unfueled, a refuelable one is when out of fuel.
    [Fact]
    public void OnlyARefuelableBuildingOutOfFuelIsUnfueled()
    {
        var site = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 0, powered: false);

        Assert.False(site.Unfueled);
        Assert.False((site with { Service = site.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 5, TargetFuel = 30, AutoRefuelPercent = 0.3, OutOfFuel = false } } }).Unfueled);
        Assert.True((site with { Service = site.Service with { Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 30, AutoRefuelPercent = 0.3, OutOfFuel = true } } }).Unfueled);
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

}
