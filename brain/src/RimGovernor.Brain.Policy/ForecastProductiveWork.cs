using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Conditional learning from the same completed physical work, never terminal utility.</summary>
public sealed record ForecastProductiveLearning(string Skill, SkillXpEvent Callback, SkillProjection Before,
    SkillProjection After, double ObservedWorkTicks, double WorkTicks, string Provenance);
public sealed record ForecastProductiveDuration(Fact<double> WorkTicks, ForecastProductiveLearning? Evidence, string Reason);

public static partial class PolicyFunctions
{
    /// <summary>Resolve duration before scheduling occupancy, ingredients or downstream output. Completion-only
    /// recipes can change skill only at completion; native decay can change rate during their work.</summary>
    public static ForecastProductiveDuration ForecastProductiveWork(ForecastActivityOption option,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        if (option.SkillWork is not { } work) return new(option.WorkTicks, null, "learning_effect_unassessed");
        ForecastProductiveDuration Unknown(string reason) => new(default, null, reason);
        var mechanics = work.Learning;
        if (!double.IsFinite(start) || start < 0 || start > GameTime.TicksPerDay
            || work.Recipe.DuringWork || !work.Recipe.RawXpPerWorkTick.IsKnown
            || !work.WorkAmount.TryGetValue(out var remaining) || !double.IsFinite(remaining) || remaining <= 0
            || !mechanics.ValidForTicks.IsKnown || !mechanics.DecayInterval.IsKnown || mechanics.DecayInterval.Value <= 0
            || work.Rates.Select(r => r.StoredLevel).Distinct().Count() != work.Rates.Count)
            return Unknown("productive_mechanics_unknown");
        var events = assigned.Where(a => a.Pawn == option.Pawn && a.Learning?.Skill == work.Recipe.Skill)
            .Select(a => a.Learning!.Callback).OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var tick = (int)Math.Ceiling(start);
        if (events.Any(e => e.Tick > tick)) return Unknown("learning_callback_after_work_start");
        var before = ForecastSkill(mechanics, events, tick);
        var projection = before;
        var worked = 0;
        // One segment per native decay interval, bounded by the existing one-day validity window.
        while (remaining > 0)
        {
            if (!projection.StoredLevel.TryGetValue(out var level)) return Unknown(string.Join(",", projection.Findings));
            var rate = work.Rates.SingleOrDefault(r => r.StoredLevel == level)?.WorkPerTick ?? default;
            if (!rate.TryGetValue(out var speed) || !double.IsFinite(speed) || speed <= 0)
                return Unknown("skill_work_rate_unknown");
            var nextDecay = mechanics.NextDecay.Value > tick ? mechanics.NextDecay.Value
                : tick + mechanics.DecayInterval.Value - (tick - mechanics.NextDecay.Value) % mechanics.DecayInterval.Value;
            var bound = Math.Min(mechanics.ValidForTicks.Value - 1, nextDecay);
            var needed = Math.Ceiling(remaining / speed);
            var segment = (int)Math.Min(needed, Math.Max(0, bound - tick));
            if (segment <= 0) return Unknown("productive_learning_window_expired");
            remaining -= segment * speed; worked += segment; tick += segment;
            projection = ForecastSkill(mechanics, events, tick);
        }
        var callback = RecipeCompletionLearning(work.Recipe, option.Activity, tick, worked, true);
        events.Add(callback);
        var after = ForecastSkill(mechanics, events, tick);
        if (!after.StoredLevel.IsKnown) return Unknown(string.Join(",", after.Findings));
        var duration = tick - start;
        return new(Known(duration), new(work.Recipe.Skill, callback, before, after,
            option.WorkTicks.IsKnown ? option.WorkTicks.Value : duration, duration, work.Provenance), "conditional_productive_learning");
    }
}
