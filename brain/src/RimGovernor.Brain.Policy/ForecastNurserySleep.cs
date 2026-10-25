using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The cry's shared SleepDisturbed pool (#3942), separate from the fit's per-baby memories.</summary>
internal sealed class ForecastNurserySleep(ForecastCoupledContext context)
{
    private readonly Dictionary<string, List<(HeardMemoryRead Memory, double Rate)>> with = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(HeardMemoryRead Memory, double Rate)>> without = new(StringComparer.Ordinal);

    internal void Add(BabyRead baby,
        IReadOnlyDictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range> withRates,
        IReadOnlyDictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range> withoutRates)
    {
        // Native owns eligibility and memory mechanics. The canonical presence owner owns every sleeping
        // location and time share; target rows must not independently allocate another day of sleep.
        foreach (var group in baby.SleepHearers.GroupBy(h => (h.Pawn, h.Memory.Fit)))
        {
            var definitions = group.Select(h => h.Memory).Distinct().ToArray();
            if (definitions.Length != 1) throw new RequiredFactException("sleep hearer memory:" + group.Key);
            var memory = definitions[0];
            var key = (baby.Pawn, group.Key.Pawn, memory.Fit, true);
            var withRate = withRates.GetValueOrDefault(key); var withoutRate = withoutRates.GetValueOrDefault(key);
            Add(with, group.Key.Pawn, memory, memory.Offset < 0 ? withRate.Upper : withRate.Lower);
            Add(without, group.Key.Pawn, memory, memory.Offset < 0 ? withoutRate.Lower : withoutRate.Upper);
        }
    }
    private static void Add(Dictionary<string, List<(HeardMemoryRead, double)>> into, string pawn, HeardMemoryRead memory, double rate)
    {
        if (!into.TryGetValue(pawn, out var pool)) into[pawn] = pool = [];
        pool.Add((memory, rate));
    }

    internal IReadOnlyDictionary<string, (double With, double Without)> Moods()
    {
        var interval = context.Needs.Babies.SleepDisturbanceIntervalTicks;
        if (!double.IsFinite(interval) || interval <= 0) throw new RequiredFactException("sleep disturbance interval");
        double Mood(List<(HeardMemoryRead Memory, double Rate)> pool)
        {
            if (pool.Count == 0) return 0;
            var memory = pool[0].Memory;
            if (pool.Any(p => p.Memory.Thought != memory.Thought || p.Memory.Offset != memory.Offset || p.Memory.DurationTicks != memory.DurationTicks
                || p.Memory.StackLimit != memory.StackLimit || p.Memory.StackedMultiplier != memory.StackedMultiplier))
                throw new RequiredFactException("shared disturbed sleep memory");
            // Canonical located sleep spans thin cries; the resulting mean arrivals are treated as Poisson.
            // Pawn's shared cooldown is a non-paralyzable renewal dead time: admitted rate = rate/(1+rate*interval).
            // Pool before stacking: TryGainMemory(SleepDisturbed) has no otherPawn, even for different crying babies.
            var rate = pool.Sum(p => p.Rate);
            return ForecastNursery.HeardMood([(memory, rate / (1 + rate * interval))]);
        }
        return with.Keys.Concat(without.Keys).Distinct(StringComparer.Ordinal).ToDictionary(p => p,
            p => (Mood(with.GetValueOrDefault(p) ?? []), Mood(without.GetValueOrDefault(p) ?? [])), StringComparer.Ordinal);
    }
}
