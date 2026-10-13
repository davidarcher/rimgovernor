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
    public int CurrentTwelfth { get; init; }
    public long TicksToNextTwelfth { get; init; }
    public GearWeather? Weather { get; init; }
}

public static class GearClimateExtensions
{
    /// <summary>Throws <see cref="DomainException"/> where Go returned an error. A null climate is valid.</summary>
    public static void Validate(this GearClimate? c)
    {
        if (c is null)
        {
            return;
        }
        DomainException.Require(
            c.Temperatures.Count == 12 && c.CurrentTwelfth >= 0 && c.CurrentTwelfth < 12 && c.TicksToNextTwelfth > 0 && c.TicksToNextTwelfth <= PolicyFunctions.GearTwelfthTicks,
            "invalid gear seasonal curve"
        );
        foreach (var n in c.Temperatures)
        {
            DomainException.Require(double.IsFinite(n), "invalid gear seasonal temperature");
        }
        if (c.Weather is { } w)
        {
            DomainException.Require(
                w.Definition.Length != 0 && w.RemainingTicks >= -1 && double.IsFinite(w.TemperatureOffset) && w.TemperatureOffset != 0,
                "invalid gear weather condition"
            );
        }
    }

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
                start = c.TicksToNextTwelfth + (long)(ahead - 1) * PolicyFunctions.GearTwelfthTicks;
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

public static partial class PolicyFunctions
{
    internal const long GearTwelfthTicks = 300000;
}
