using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PowerMethodTests
{
    private static readonly PowerPlanning Planning = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { ["WoodFiredGenerator"] = PolicyConstants.ConstantPowerProfile },
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

        var proposal = SelectPowerMethod(Known(topology), new Bounds(20, 20), [], [], Planning);

        Assert.Equal(PowerMethod.WaitFuel, proposal.Method);
        Assert.Equal("stove", proposal.Target);
    }
}
