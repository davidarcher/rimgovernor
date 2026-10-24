namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a change in one organ's efficiency does to the pawn (#3830), mirroring PawnCapacityUtility and the capacity workers: the
/// organ's tag efficiency is the mean of its parts' (CalculateTagEfficiency), so the capacity the tag feeds (Breathing,
/// BloodPumping, BloodFiltration) moves by the new mean over the old; consciousness lerps that capacity in
/// (PawnCapacityWorker_Consciousness), moving lerps Breathing and BloodPumping in and multiplies min(consciousness, 1)
/// (PawnCapacityWorker_Moving), manipulation and talking multiply consciousness, and sight is untouched. The constants are in
/// kept-constants, "Mirrored game literals: illness organ loss".
/// </summary>
internal static class OrganCapacity
{
    // PawnCapacityWorker_Consciousness: Lerp(num, num × min(level, 1), weight).
    internal const double ConsciousnessPumping = 0.2;
    internal const double ConsciousnessBreathing = 0.2;
    internal const double ConsciousnessFiltration = 0.1;

    // PawnCapacityWorker_Moving: Lerp(num, num × level, 0.2) for Breathing and BloodPumping.
    internal const double MovingBreathing = 0.2;
    internal const double MovingPumping = 0.2;

    /// <summary>
    /// The capacity <paramref name="level"/> once part <paramref name="index"/> of the tag's parts (<paramref name="efficiencies"/>,
    /// CalculatePartEfficiency each) gives <paramref name="next"/>; unchanged when the tag's parts give nothing now.
    /// </summary>
    internal static double Level(double level, IReadOnlyList<double> efficiencies, int index, double next)
    {
        var total = efficiencies.Sum();
        return total > 0 ? level * Math.Max(0, total + next - efficiencies[index]) / total : level;
    }

    /// <summary>
    /// The factors on the five pawn-day levels (ForecastValue's order: consciousness, moving, manipulation, sight, talking) of
    /// <paramref name="capacity"/> going from <paramref name="level"/> to <paramref name="next"/>, for a pawn of
    /// <paramref name="consciousness"/>. The capacity names are PawnCapacityDef defNames; the policy has no typed route to them.
    /// </summary>
    internal static double[] Factors(string capacity, double level, double next, double consciousness)
    {
        var (lerped, moved) = capacity switch
        {
            "BloodPumping" => (ConsciousnessPumping, MovingPumping),
            "Breathing" => (ConsciousnessBreathing, MovingBreathing),
            "BloodFiltration" => (ConsciousnessFiltration, 0.0),
            _ => throw new RequiredFactException("organ_capacity:" + capacity),
        };
        var awake = (1 - (lerped * (1 - Math.Min(next, 1)))) / (1 - (lerped * (1 - Math.Min(level, 1))));
        var moving = (1 + (moved * (next - 1))) / (1 + (moved * (level - 1)))
            * (consciousness > 0 ? Math.Min(consciousness * awake, 1) / Math.Min(consciousness, 1) : 1);
        return [awake, moving, awake, 1, awake];
    }
}
