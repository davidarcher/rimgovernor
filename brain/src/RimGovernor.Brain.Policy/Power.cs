using RimGovernor.Brain.Domain;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The grid's coverage of its consumers: whether any consumer draws power, the lowest output on a consumer's own
/// network (-1 for a consumer that is disconnected or unpowered, 0 with no consumer), and whether an enabled consumer
/// is unpowered.
/// </summary>
public readonly record struct PowerCoverage(bool Required, double HeadroomW, bool Disabled);

public static partial class PowerRules
{
    public static double ReserveDays(this PowerNetworkFact network)
    {
        var drain = network.ConsumptionW - network.GenerationW;
        return drain <= 0 ? double.PositiveInfinity : network.StoredWD / drain;
    }

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
