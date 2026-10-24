using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole building-census service rows for the decoder tests.</summary>
internal static class BuildingServiceWire
{
    /// <summary>A building with no power comp, no refuelable comp and no wind path: switched on and intact.</summary>
    internal static readonly O.BuildingServiceState NoService = new()
    {
        Power = new O.BuildingPower.NoPower(),
        SwitchedOn = true,
        Fuel = new O.BuildingFuel.NotRefuelable(),
        BrokenDown = false,
        Wind = new O.BuildingWind.NotWindTurbine(),
    };
}
