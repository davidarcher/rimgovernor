using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PowerMethodTests
{
    private static readonly PowerPlanning Planning = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { ["WoodFiredGenerator"] = PowerRules.ConstantPowerProfile },
    };

    private static PowerSite Site(string id, string definition, Cell cell, double watts, double output, bool powered) => new()
    {
        Id = id,
        Definition = definition,
        Cell = cell,
        Occupied = [cell],
        BaseW = Known(watts),
        OutputW = output,
        Connected = true,
        Network = "net",
        Powered = powered,
        Forbidden = false,
        SwitchedOn = true,
    };

    // power/fuel's native half stages the generator and the refuel; this is the planner's half: an empty generator that
    // could carry the stove holds for ordinary refuelling instead of building another generator or conduit.
    [Fact]
    public void OutOfFuelProducerWithCapacityHoldsForRefuel()
    {
        var generator = Site("gen", "WoodFiredGenerator", new(2, 3), 1000, 0, powered: false) with { Fuel = Known(0.0), OutOfFuel = Known(true) };
        var stove = Site("stove", "ElectricStove", new(8, 2), -350, 0, powered: false);
        var topology = new PowerTopology { Buildings = [generator, stove], Blackout = Known(false), Eclipse = Known(false) };

        var proposal = PowerRules.SelectPowerMethod(Known(topology), new Bounds(20, 20), [], [], Planning);

        Assert.Equal(PowerMethod.WaitFuel, proposal.Method);
        Assert.Equal("stove", proposal.Target);
    }

    // #2799: a polar-night twelfth gives a solar generator nothing to deliver, so the wind turbine outranks it; an unread
    // daylight curve keeps the list order, and a lit twelfth leaves solar first.
    [Fact]
    public void WindOutranksSolarAtZeroDaylight()
    {
        GeneratorOption[] options =
        [
            new() { Definition = "SolarGenerator", Available = Known(true), Profile = PowerRules.SolarPowerProfile, NominalW = 1700 },
            new() { Definition = PowerRules.WindTurbineDefinition, Available = Known(true), Profile = PowerRules.WindProfile(0.04, 2, 1), NominalW = 2300 },
        ];
        var ranking = new GeneratorRanking { BatteryAvailable = Known(true) };

        Assert.Equal(["SolarGenerator", PowerRules.WindTurbineDefinition], PowerRules.RankGenerators(options, ranking));
        Assert.Equal(["SolarGenerator", PowerRules.WindTurbineDefinition], PowerRules.RankGenerators(options, ranking with { DaylightHours = Known(12.0) }));
        Assert.Equal([PowerRules.WindTurbineDefinition, "SolarGenerator"], PowerRules.RankGenerators(options, ranking with { DaylightHours = Known(0.0) }));
    }
}
