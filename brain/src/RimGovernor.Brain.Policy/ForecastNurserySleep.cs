using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The cry's shared SleepDisturbed pool (#3942), separate from the fit's per-baby memories.</summary>
internal sealed class ForecastNurserySleep(ForecastCoupledContext context, ForecastCoupledOutcome outcome)
{
    private readonly Dictionary<string, List<(HeardMemoryRead Memory, double Rate)>> with = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(HeardMemoryRead Memory, double Rate)>> without = new(StringComparer.Ordinal);

    internal void Add(BabyRead baby, IReadOnlyDictionary<string, double> rates, IReadOnlyDictionary<string, double> cribRates)
    {
        foreach (var group in baby.SleepHearers.GroupBy(h => h.Pawn, StringComparer.Ordinal))
        {
            var pawn = context.Needs.Pawns.SingleOrDefault(p => p.Pawn == group.Key) ?? throw new RequiredFactException("sleep hearer needs:" + group.Key);
            var surface = pawn.Sleep ?? throw new RequiredFactException("sleep hearer surface:" + pawn.Pawn);
            // A selected terminal rest surface changes future sleep. No rest allocation means the native default
            // surface, not a pawn that never sleeps over the investment horizon.
            var selected = (from allocation in outcome.Plan.Allocations
                join task in context.NeedTasks on allocation.Activity equals task.Task
                where task.Pawn == pawn.Pawn && task.Kind == "rest" && allocation.End > allocation.Start + task.ServiceDelay
                orderby allocation.End descending, allocation.Activity
                select task.Target).FirstOrDefault();
            var target = selected ?? surface.Bed;
            var gain = surface.GainPerTick;
            if (selected is not null)
            {
                var row = pawn.Targets.SingleOrDefault(t => t.Kind == "rest" && t.Id == selected)
                    ?? throw new RequiredFactException("selected sleep target:" + pawn.Pawn + ":" + selected);
                gain = row.Recovery is NeedRecovery.Rate rate ? rate.GainPerTick
                    : throw new RequiredFactException("selected sleep recovery:" + selected);
            }
            var rest = pawn.Needs.SingleOrDefault(n => n.Kind == "rest") ?? throw new RequiredFactException("sleep hearer rest:" + pawn.Pawn);
            if (!double.IsFinite(gain) || gain <= 0 || !double.IsFinite(rest.FallPerTick) || rest.FallPerTick < 0)
                throw new RequiredFactException("sleep hearer rest rates:" + pawn.Pawn);
            var asleep = rest.FallPerTick / (gain + rest.FallPerTick);
            var hearing = group.Where(h => h.Target == target).ToArray();
            if (hearing.Length == 0) throw new RequiredFactException("sleep target hearing:" + pawn.Pawn + ":" + target);
            foreach (var hearer in hearing.Where(h => rates.ContainsKey(h.Memory.Fit)))
            {
                if (baby.InNursery && hearer.Now) Add(with, pawn.Pawn, hearer.Memory, cribRates[hearer.Memory.Fit] * asleep);
                if (baby.InNursery ? hearer.NurseryOpen : hearer.Now) Add(without, pawn.Pawn, hearer.Memory, rates[hearer.Memory.Fit] * asleep);
            }
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
            // Stationary approximation: independently thin cries by asleep share, then treat arrivals as Poisson.
            // Pawn's shared cooldown is a non-paralyzable renewal dead time: admitted rate = rate/(1+rate*interval).
            // Pool before stacking: TryGainMemory(SleepDisturbed) has no otherPawn, even for different crying babies.
            var rate = pool.Sum(p => p.Rate);
            return ForecastNursery.HeardMood([(memory, rate / (1 + rate * interval))]);
        }
        return with.Keys.Concat(without.Keys).Distinct(StringComparer.Ordinal).ToDictionary(p => p,
            p => (Mood(with.GetValueOrDefault(p) ?? []), Mood(without.GetValueOrDefault(p) ?? [])), StringComparer.Ordinal);
    }
}
