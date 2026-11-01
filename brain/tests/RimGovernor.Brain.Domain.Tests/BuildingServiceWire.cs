
namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole building-census service rows for the decoder tests.</summary>
internal static class BuildingServiceWire
{
    internal static readonly RimGovernor.Brain.GameFacts.Observations.FuelBurn IdleBurn = new()
    {
        PerDay = 0, UnitsPerItem = 1, UseDriven = false, InRain = false, WhenPowered = false,
    };

    /// <summary>A building with no power comp, no refuelable comp and no wind path: switched on and intact.</summary>
    internal static readonly RimGovernor.Brain.GameFacts.Observations.BuildingServiceState NoService = new()
    {
        Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.NoPower(),
        Switch = new RimGovernor.Brain.GameFacts.Observations.BuildingSwitch.Fixed(),
        Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.NotRefuelable(),
        BrokenDown = false,
        Wind = new RimGovernor.Brain.GameFacts.Observations.BuildingWind.NotWindTurbine(),
    };

    /// <summary>A flick switch reading <paramref name="on"/>, with its gizmo wanting <paramref name="wanted"/> (the same when no flick is pending).</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.BuildingSwitch Flick(bool on, bool? wanted = null) =>
        new RimGovernor.Brain.GameFacts.Observations.BuildingSwitch.Flick { On = on, Wanted = wanted ?? on };
}
