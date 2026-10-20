using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Conditional learning from the same completed physical work, never terminal utility.</summary>
public sealed record ForecastProductiveLearning(string Skill, SkillXpEvent Callback, SkillProjection Before,
    SkillProjection After, double ObservedWorkTicks, double WorkTicks, string Provenance);
public sealed record ForecastProductiveDuration(Fact<double> WorkTicks, ForecastProductiveLearning? Evidence, string Reason);

public static class ForecastProductiveWorkPolicy
{
    /// <summary>Resolve duration before scheduling occupancy, ingredients or downstream output. Completion-only
    /// recipes can change skill only at completion; native decay can change rate during their work.</summary>
    public static ForecastProductiveDuration ForecastProductiveWork(ForecastActivityOption option,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        if (option.SkillWork is not { } work)
            return option.TendLearning is { } tend ? Tend(option, tend, assigned, start) : new(option.WorkTicks, null, "learning_effect_unassessed");
        ForecastProductiveDuration Unknown(string reason) => new(default, null, reason);
        var mechanics = work.Learning;
        if (!double.IsFinite(start) || start < 0 || start > GameTime.TicksPerDay
            || work.Recipe.DuringWork || !work.Recipe.RawXpPerWorkTick.IsKnown
            || !work.WorkAmount.TryGetValue(out var remaining) || !double.IsFinite(remaining) || remaining <= 0
            || !mechanics.ValidForTicks.IsKnown || !mechanics.DecayInterval.IsKnown || mechanics.DecayInterval.Value <= 0
            || work.Rates.Select(r => r.StoredLevel).Distinct().Count() != work.Rates.Count)
            return Unknown("productive_mechanics_unknown");
        var events = Prior(assigned, option.Pawn, work.Recipe.Skill);
        var tick = (int)Math.Ceiling(start);
        if (events.Any(e => e.Tick > tick)) return Unknown("learning_callback_after_work_start");
        var before = ForecastSkillPolicy.ForecastSkill(mechanics, events, tick);
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
            projection = ForecastSkillPolicy.ForecastSkill(mechanics, events, tick);
        }
        var callback = ForecastSkillPolicy.RecipeCompletionLearning(work.Recipe, option.Activity, tick, worked, true);
        events.Add(callback);
        var after = ForecastSkillPolicy.ForecastSkill(mechanics, events, tick);
        if (!after.StoredLevel.IsKnown) return Unknown(string.Join(",", after.Findings));
        var duration = tick - start;
        return new(Known(duration), new(work.Recipe.Skill, callback, before, after,
            option.WorkTicks.IsKnown ? option.WorkTicks.Value : duration, duration, work.Provenance), "conditional_productive_learning");
    }

    /// <summary>A tend's duration is observed and its learning never changes it: the callback is paid once at completion.
    /// Learning that cannot be projected leaves the observed duration without evidence, never removes the treatment.</summary>
    private static ForecastProductiveDuration Tend(ForecastActivityOption option, TendLearning tend,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        if (!option.WorkTicks.TryGetValue(out var work) || !double.IsFinite(work) || work < 0
            || !double.IsFinite(start) || start < 0 || start + work > GameTime.TicksPerDay)
            return new(option.WorkTicks, null, "tend_learning_outside_window");
        var tick = (int)Math.Ceiling(start);
        var events = Prior(assigned, option.Pawn, tend.Skill);
        if (events.Any(e => e.Tick > tick)) return new(option.WorkTicks, null, "learning_callback_after_work_start");
        var before = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, tick);
        var end = (int)Math.Ceiling(start + work);
        var callback = ForecastSkillPolicy.TendCompletionLearning(tend, option.Activity, end);
        events.Add(callback);
        var after = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, end);
        if (!before.StoredLevel.IsKnown || !after.StoredLevel.IsKnown)
            return new(option.WorkTicks, null, string.Join(",", before.Findings.Concat(after.Findings).Distinct()));
        return new(option.WorkTicks, new(tend.Skill, callback, before, after, work, work, tend.Provenance), "conditional_tend_learning");
    }

    private static List<SkillXpEvent> Prior(IReadOnlyList<ForecastActivityAllocation> assigned, string pawn, string skill)
        => assigned.Where(a => a.Pawn == pawn && a.Learning?.Skill == skill)
            .Select(a => a.Learning!.Callback).OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
}
