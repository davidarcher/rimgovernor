namespace RimGovernor.Brain.Policy;

/// <summary>
/// The vanilla hypothermia model (HediffGiver_Hypothermia), one step per HypothermiaInterval ticks. The values mirror
/// Verse.HealthTuning (HypothermiaGrowthPerDegreeUnder, MinHypothermiaProgress, HarmfulTemperatureOffset); the
/// native probe environment/hypothermia checks PublishedHypothermiaTuning against those constants, the pawn's safe
/// range and the severity the game grows.
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
public abstract record OutdoorBudget
{
    private OutdoorBudget() { }
    public sealed record Finite(int Ticks) : OutdoorBudget;
    public sealed record Unbounded : OutdoorBudget;
    public sealed record Unmodelled : OutdoorBudget;
}

public static class DashEstimateRules
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
    public const int HypothermiaInterval = 60;

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
    /// severity. Only cold is modelled; other hazards are explicitly unmodelled. The measurements
    /// are required. A severity already at target gets zero.
    /// </summary>
    public static OutdoorBudget OutdoorBudgetFor(
        HazardKind kind,
        double tempC,
        double comfortMinC,
        double severity,
        double target,
        HypothermiaTuning tuning)
    {
        if (kind != HazardKind.ExtremeCold)
        {
            return new OutdoorBudget.Unmodelled();
        }
        var sev = severity;
        var g = tuning.Growth(tempC, comfortMinC);
        if (g <= 0)
        {
            return new OutdoorBudget.Unbounded();
        }
        var steps = 0;
        if (sev < target)
        {
            for (var s = sev + g; s < target; s += g)
            {
                steps++;
            }
        }
        return new OutdoorBudget.Finite(steps * HypothermiaInterval);
    }
}
