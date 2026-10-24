using RimGovernor.Brain.Domain;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One power trader or battery as the development census reports it. BaseW is the def's, with its finished power
/// upgrades; Network is empty while the building is not connected.
/// </summary>
public record PowerBuilding
{
    public bool RainVulnerable { get; init; }

    public bool Roofed { get; init; }

    public double BaseW { get; init; }

    public double OutputW { get; init; }

    public bool Powered { get; init; }

    public bool Connected { get; init; }

    public bool Forbidden { get; init; }

    public bool SwitchedOn { get; init; }

    public string Network { get; init; } = "";

    /// <summary>The building's refuelable comp: its fuel, target and whether it is out of fuel, or not refuelable.</summary>
    public required O.BuildingFuel Fuel { get; init; }

    /// <summary>
    /// <see cref="Fuel"/>'s out-of-fuel flag, unknown on a building that is not refuelable. Only the fuel consumer
    /// rows (Observation's Fuel) read it; the defense turret facts read <see cref="Unfueled"/>.
    /// </summary>
    public Fact<bool> OutOfFuel { get; init; }

    public bool BrokenDown { get; init; }

    public IReadOnlyList<string> FuelDefinitions { get; init; } = [];

    /// <summary>The energy the building's battery comp stores at most (storedEnergyMax); a building without one stores none.</summary>
    public double StorageWD { get; init; }

    /// <summary>The turret gun the building carries; null for every other consumer.</summary>
    public TurretGun? Turret { get; init; }

    /// <summary>Whether the building is a refuelable one that is out of fuel.</summary>
    public bool Unfueled => Fuel is O.BuildingFuel.Refuelable { OutOfFuel: true };
}

/// <summary>A turret gun's observed damage per second, from its own verb and burst timing.</summary>
public sealed record TurretGun(double Dps);

/// <summary>
/// One native power net's same-tick energy summary. Reserve runway is derived, never observed: stored energy
/// over net drain.
/// </summary>
public sealed record PowerNetworkFact
{
    public string Id { get; init; } = "";

    public double GenerationW { get; init; }

    public double ConsumptionW { get; init; }

    public double StoredWD { get; init; }

    /// <summary>The number of days the network's stored energy covers its current net drain; +Inf when the network is not draining.</summary>
    public double ReserveDays()
    {
        var drain = ConsumptionW - GenerationW;
        return drain <= 0 ? double.PositiveInfinity : StoredWD / drain;
    }
}

/// <summary>
/// The grid's coverage of its consumers: whether any consumer draws power, the lowest output on a consumer's own
/// network (-1 for a consumer that is disconnected or unpowered, 0 with no consumer), and whether an enabled consumer
/// is unpowered.
/// </summary>
public readonly record struct PowerCoverage(bool Required, double HeadroomW, bool Disabled);

public static partial class PowerRules
{
    /// <summary>Requires actual roof coverage of rain-sensitive equipment. Empty batteries still need shelter before the network charges them.</summary>
    public static bool PowerWeatherSafe(IReadOnlyList<PowerBuilding> buildings) =>
        buildings.All(b => !b.RainVulnerable || b.Roofed);

    /// <summary>
    /// Evaluates each consumer's own network. Spare generation on a different network cannot cover it;
    /// disconnected or unpowered consumers fail.
    /// </summary>
    public static PowerCoverage PowerCoverageOf(IReadOnlyList<PowerBuilding> buildings)
    {
        var consumers = buildings.Where(b => b.BaseW < 0).ToList();
        if (consumers.Count == 0)
        {
            return new PowerCoverage(false, 0, false);
        }
        var minimum = double.PositiveInfinity;
        var disabled = false;
        foreach (var consumer in consumers)
        {
            // An enabled, unpowered consumer is a recovery target.
            disabled = disabled || (!consumer.Powered && !consumer.Forbidden && consumer.SwitchedOn);
            double value = -1;
            if (consumer.Connected && consumer.Powered)
            {
                value = 0;
                foreach (var b in buildings)
                {
                    if (b.Connected && b.Network == consumer.Network)
                    {
                        value += b.OutputW;
                    }
                }
            }
            minimum = Math.Min(minimum, value);
        }
        return new PowerCoverage(true, minimum, disabled);
    }
}
