using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public record PowerBuilding
{
    public Fact<bool> RainVulnerable { get; init; }

    public Fact<bool> Roofed { get; init; }

    public Fact<double> BaseW { get; init; }

    public Fact<double> OutputW { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<bool> Connected { get; init; }

    public Fact<bool> Forbidden { get; init; }

    public Fact<bool> SwitchedOn { get; init; }

    public Fact<string> Network { get; init; }

    // Refuelable/breakdown service facts are unknown for buildings without the matching native component; a
    // battery reports BaseW 0 plus Stored/Capacity.
    public Fact<double> Fuel { get; init; }

    public Fact<double> TargetFuel { get; init; }

    public Fact<bool> OutOfFuel { get; init; }

    public Fact<bool> BrokenDown { get; init; }

    public IReadOnlyList<string> FuelDefinitions { get; init; } = [];

    public Fact<double> Stored { get; init; }

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

    public Fact<double> GenerationW { get; init; }

    public Fact<double> ConsumptionW { get; init; }

    public Fact<double> StoredWD { get; init; }

    public Fact<double> CapacityWD { get; init; }

    /// <summary>
    /// The number of days the network's stored energy covers its current net drain. Unknown when any input is
    /// unknown; +Inf when the network is not draining.
    /// </summary>
    public Fact<double> ReserveDays()
    {
        var gk = GenerationW.TryGetValue(out var generation);
        var ck = ConsumptionW.TryGetValue(out var consumption);
        var sk = StoredWD.TryGetValue(out var stored);
        if (!gk || !ck || !sk)
        {
            return Unknown<double>();
        }
        var drain = consumption - generation;
        return drain <= 0 ? Known(double.PositiveInfinity) : Known(stored / drain);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Requires actual roof coverage of rain-sensitive equipment. Empty batteries still need shelter before the network charges them.</summary>
    public static Fact<bool> PowerWeatherSafe(IReadOnlyList<PowerBuilding> buildings)
    {
        var known = true;
        foreach (var b in buildings)
        {
            var vk = b.RainVulnerable.TryGetValue(out var vulnerable);
            if (vk && !vulnerable)
            {
                continue;
            }
            var rk = b.Roofed.TryGetValue(out var roofed);
            if (vk && rk && !roofed)
            {
                return Known(false);
            }
            known = known && vk && rk;
        }
        return known ? Known(true) : Unknown<bool>();
    }

    /// <summary>
    /// Evaluates each consumer's own network. Spare generation on a different network cannot cover it;
    /// disconnected or unpowered consumers fail. Any result left Unknown could not be decided.
    /// </summary>
    public static (Fact<bool> Required, Fact<double> Headroom, Fact<bool> Disabled) PowerCoverage(Fact<EquatableArray<PowerBuilding>> rows)
    {
        Fact<bool> required = default;
        Fact<double> headroom = default;
        Fact<bool> disabled = default;
        if (!rows.TryGetValue(out var buildings))
        {
            return (required, headroom, disabled);
        }
        var consumers = new List<PowerBuilding>();
        foreach (var b in buildings)
        {
            if (!b.BaseW.TryGetValue(out var baseW))
            {
                return (required, headroom, disabled);
            }
            if (baseW < 0)
            {
                consumers.Add(b);
            }
        }
        required = Known(consumers.Count > 0);
        if (consumers.Count == 0)
        {
            return (required, Known(0.0), Known(false));
        }
        double minimum = 0;
        bool haveMinimum = false, powerKnown = true;
        bool disabledValue = false, disabledKnown = true;
        foreach (var consumer in consumers)
        {
            var onKnown = consumer.Powered.TryGetValue(out var on);
            var fk = consumer.Forbidden.TryGetValue(out var forbidden);
            var sk = consumer.SwitchedOn.TryGetValue(out var switched);
            if ((onKnown && on) || (fk && forbidden) || (sk && !switched))
            {
                // This consumer is not an enabled, unpowered recovery target.
            }
            else if (onKnown && fk && sk)
            {
                disabledValue = true;
            }
            else
            {
                disabledKnown = false;
            }
            var ck = consumer.Connected.TryGetValue(out var connected);
            double value = -1;
            if ((ck && !connected) || (onKnown && !on))
            {
                // Known failure dominates unknown output or network identity.
            }
            else if (!ck || !onKnown)
            {
                powerKnown = false;
                continue;
            }
            else
            {
                if (!consumer.Network.TryGetValue(out var network))
                {
                    powerKnown = false;
                    continue;
                }
                value = 0;
                foreach (var b in buildings)
                {
                    var known = b.Connected.TryGetValue(out var bConnected);
                    if (known && !bConnected)
                    {
                        continue;
                    }
                    var nk = b.Network.TryGetValue(out var net);
                    if (!known || !nk)
                    {
                        powerKnown = false;
                        continue;
                    }
                    if (net == network)
                    {
                        if (!b.OutputW.TryGetValue(out var output))
                        {
                            powerKnown = false;
                        }
                        else
                        {
                            value += output;
                        }
                    }
                }
            }
            if (!haveMinimum || value < minimum)
            {
                minimum = value;
                haveMinimum = true;
            }
        }
        if (powerKnown && haveMinimum)
        {
            headroom = Known(minimum);
        }
        if (disabledValue || disabledKnown)
        {
            disabled = Known(disabledValue);
        }
        return (required, headroom, disabled);
    }
}
