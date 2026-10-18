namespace RimGovernor.Brain.Policy;

public sealed record GearWeather
{
    public string Definition { get; init; } = "";

    /// <summary>-1 means permanent.</summary>
    public long RemainingTicks { get; init; }

    public double TemperatureOffset { get; init; }
}

/// <summary>Optional. A null value preserves ambient-only scoring.</summary>
public sealed record GearClimate
{
    public EquatableArray<double> Temperatures { get; init; }

    /// <summary>Hours of daylight on the sampled day of each twelfth, indexed like <see cref="Temperatures"/>; empty when the read did not carry it (#3526).</summary>
    public EquatableArray<double> DaylightHours { get; init; }

    public int CurrentTwelfth { get; init; }
    public long TicksToNextTwelfth { get; init; }
    public GearWeather? Weather { get; init; }
}

public static class GearClimateExtensions
{
    /// <summary>Throws <see cref="DomainException"/>. A null climate is valid.</summary>
    public static void Validate(this GearClimate? c)
    {
        if (c is null)
        {
            return;
        }
        DomainException.Require(
            c.Temperatures.Count == 12 && c.CurrentTwelfth >= 0 && c.CurrentTwelfth < 12 && c.TicksToNextTwelfth > 0 && c.TicksToNextTwelfth <= GearClimateRules.GearTwelfthTicks,
            "invalid gear seasonal curve"
        );
        foreach (var n in c.Temperatures)
        {
            DomainException.Require(double.IsFinite(n), "invalid gear seasonal temperature");
        }
        DomainException.Require(
            c.DaylightHours.Count == 0 || c.DaylightHours.Count == 12 && c.DaylightHours.All(h => double.IsFinite(h) && h >= 0 && h <= 24),
            "invalid gear daylight hours"
        );
        if (c.Weather is { } w)
        {
            DomainException.Require(
                w.Definition.Length != 0 && w.RemainingTicks >= -1 && double.IsFinite(w.TemperatureOffset) && w.TemperatureOffset != 0,
                "invalid gear weather condition"
            );
        }
    }

    /// <summary>The hours of daylight on the given twelfth's sampled day; unknown when the read did not carry the curve.</summary>
    public static Fact<double> DaylightHoursIn(this GearClimate c, int twelfth) =>
        c.DaylightHours.Count == 12 ? Fact.Known(c.DaylightHours[twelfth]) : Fact.Unknown<double>();

    /// <summary>
    /// Includes now and the next two twelfths (ten days of tailoring lead time). Weather affects only twelfths it
    /// still overlaps; longer conditions extend the target through their remaining duration.
    /// </summary>
    public static (double Low, double High) TemperatureRange(this GearClimate? c, double ambient)
    {
        var low = ambient;
        var high = ambient;
        if (c is null)
        {
            return (low, high);
        }
        for (var ahead = 0; ahead < 12; ahead++)
        {
            long start = 0;
            if (ahead > 0)
            {
                start = c.TicksToNextTwelfth + (long)(ahead - 1) * GearClimateRules.GearTwelfthTicks;
            }
            var w = c.Weather;
            var active = w is not null && (w.RemainingTicks == -1 || w.RemainingTicks > start);
            if (ahead > 2 && !active)
            {
                break;
            }
            var temperature = c.Temperatures[(c.CurrentTwelfth + ahead) % 12];
            if (ahead <= 2)
            {
                low = Math.Min(low, temperature);
                high = Math.Max(high, temperature);
            }
            if (active)
            {
                temperature += w!.TemperatureOffset;
                low = Math.Min(low, temperature);
                high = Math.Max(high, temperature);
            }
        }
        return (low, high);
    }
}

public static class GearClimateRules
{
    internal const long GearTwelfthTicks = 300000;
}
