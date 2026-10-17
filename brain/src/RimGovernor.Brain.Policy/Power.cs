using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One power trader or battery as the development census reports it. BaseW is the def's (unknown when the
/// catalog cannot say); Network is empty while the building is not connected.
/// </summary>
public record PowerBuilding
{
    public bool RainVulnerable { get; init; }

    public bool Roofed { get; init; }

    public Fact<double> BaseW { get; init; }

    public double OutputW { get; init; }

    public bool Powered { get; init; }

    public bool Connected { get; init; }

    public bool Forbidden { get; init; }

    public bool SwitchedOn { get; init; }

    public string Network { get; init; } = "";

    // Refuel service facts are unknown for buildings without a refuelable component; a battery reports BaseW 0
    // plus its def Capacity.
    public Fact<double> Fuel { get; init; }

    public Fact<double> TargetFuel { get; init; }

    public Fact<bool> OutOfFuel { get; init; }

    public bool BrokenDown { get; init; }

    public IReadOnlyList<string> FuelDefinitions { get; init; } = [];

    public Fact<double> Capacity { get; init; }

    /// <summary>A turret gun's observed damage per second, unknown for every other consumer.</summary>
    public Fact<double> TurretDps { get; init; }
}

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

public static partial class PolicyFunctions
{
    /// <summary>Requires actual roof coverage of rain-sensitive equipment. Empty batteries still need shelter before the network charges them.</summary>
    public static bool PowerWeatherSafe(IReadOnlyList<PowerBuilding> buildings) =>
        buildings.All(b => !b.RainVulnerable || b.Roofed);

    /// <summary>
    /// Evaluates each consumer's own network. Spare generation on a different network cannot cover it;
    /// disconnected or unpowered consumers fail. Every result is unknown while any building's base wattage is.
    /// </summary>
    public static (Fact<bool> Required, Fact<double> Headroom, Fact<bool> Disabled) PowerCoverage(IReadOnlyList<PowerBuilding> buildings)
    {
        var consumers = new List<PowerBuilding>();
        foreach (var b in buildings)
        {
            if (!b.BaseW.TryGetValue(out var baseW))
            {
                return default;
            }
            if (baseW < 0)
            {
                consumers.Add(b);
            }
        }
        var required = Known(consumers.Count > 0);
        if (consumers.Count == 0)
        {
            return (required, Known(0.0), Known(false));
        }
        var minimum = double.PositiveInfinity;
        var disabledValue = false;
        foreach (var consumer in consumers)
        {
            // An enabled, unpowered consumer is a recovery target.
            disabledValue = disabledValue || (!consumer.Powered && !consumer.Forbidden && consumer.SwitchedOn);
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
        return (required, Known(minimum), Known(disabledValue));
    }
}
