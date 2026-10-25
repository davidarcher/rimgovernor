
namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole building-census service rows for the decoder tests.</summary>
internal static class BuildingServiceWire
{
    internal static readonly RimGovernor.Brain.GameFacts.Observations.FuelBurn IdleBurn = new()
    {
        PerDay = 0, UnitsPerItem = 1, UseDriven = false, InRain = false, WhenPowered = false, Flickable = false,
    };

    /// <summary>A building with no power comp, no refuelable comp and no wind path: switched on and intact.</summary>
    internal static readonly RimGovernor.Brain.GameFacts.Observations.BuildingServiceState NoService = new()
    {
        Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.NoPower(),
        SwitchedOn = true,
        Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.NotRefuelable(),
        BrokenDown = false,
        Wind = new RimGovernor.Brain.GameFacts.Observations.BuildingWind.NotWindTurbine(),
    };
}
