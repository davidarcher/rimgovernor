namespace RimGovernor.Brain.Policy;

/// <summary>
/// The vanilla hypothermia model, one step per HypothermiaInterval ticks. The values mirror Verse.HealthTuning
/// (HypothermiaGrowthPerDegreeUnder, MinHypothermiaProgress, HarmfulTemperatureOffset; defs.proto
/// HealthTuningConstants), which no reader consumes yet: PublishedHypothermiaTuning is the RimWorld wiki
/// formula, and the native probe checks it against the game.
/// </summary>
public sealed record HypothermiaTuning
{
    public double GrowthPerDegreeUnder { get; init; }

    public double MinProgress { get; init; }

    public double HarmfulOffsetC { get; init; }

    /// <summary>The severity added per step at <paramref name="tempC"/>, 0 when the temperature is not harmful for a pawn comfortable down to <paramref name="comfortMinC"/>.</summary>
    public double Growth(double tempC, double comfortMinC)
    {
        var below = comfortMinC - tempC;
        if (below <= HarmfulOffsetC)
        {
            return 0;
        }
        return Math.Max(MinProgress, (below - HarmfulOffsetC) * GrowthPerDegreeUnder);
    }
}

/// <summary>How long a pawn may stay outdoors. Unbounded means the hazard does not grow at this temperature.</summary>
public sealed record OutdoorBudget
{
    public int Ticks { get; init; }

    public bool Unbounded { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The published formula: onset below comfortable minimum minus 10 C, growth max(0.00075, (degrees below
    /// comfortable - 10) x 0.0000645) per step.
    /// </summary>
    public static readonly HypothermiaTuning PublishedHypothermiaTuning = new()
    {
        GrowthPerDegreeUnder = 0.0000645,
        MinProgress = 0.00075,
        HarmfulOffsetC = 10,
    };

    /// <summary>The ticks between severity updates.</summary>
    internal const int HypothermiaInterval = 60;

    // Hypothermia stage floors (severity): a trip target is one of these.
    public const double HypothermiaShivering = 0.04;
    public const double HypothermiaMinor = 0.20;
    public const double HypothermiaSerious = 0.35;
    public const double HypothermiaExtreme = 0.62;
    public const double HypothermiaDeath = 1.0;

    /// <summary>
    /// The severity shed per step while not harmed: max(0.0015, min(0.015, 0.027 x severity)). Recovery is
    /// slow, so a trip starts from the severity the previous one left.
    /// </summary>
    public static double HypothermiaRecovery(double severity) => Math.Max(0.0015, Math.Min(0.015, 0.027 * severity));

    /// <summary>
    /// The ticks a pawn can stay outdoors before its hypothermia reaches target (a stage floor), starting at
    /// severity. Only cold is modelled; heat and fallout are unknown. Unknown temperature, comfort or severity
    /// is unknown. A severity already at target gets zero.
    /// </summary>
    public static Fact<OutdoorBudget> OutdoorBudgetFor(
        HazardKind kind,
        Fact<double> tempC,
        Fact<double> comfortMinC,
        Fact<double> severity,
        double target,
        HypothermiaTuning tuning)
    {
        if (kind != HazardKind.ExtremeCold)
        {
            return Unknown<OutdoorBudget>();
        }
        var tk = tempC.TryGetValue(out var temp);
        var ck = comfortMinC.TryGetValue(out var comfort);
        var sk = severity.TryGetValue(out var sev);
        if (!tk || !ck || !sk)
        {
            return Unknown<OutdoorBudget>();
        }
        var g = tuning.Growth(temp, comfort);
        if (g <= 0)
        {
            return Known(new OutdoorBudget { Unbounded = true });
        }
        var steps = 0;
        if (sev < target)
        {
            for (var s = sev + g; s < target; s += g)
            {
                steps++;
            }
        }
        return Known(new OutdoorBudget { Ticks = steps * HypothermiaInterval });
    }
}
