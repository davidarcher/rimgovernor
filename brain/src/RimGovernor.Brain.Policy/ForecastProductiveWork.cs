using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>Learning callbacks from the same physical work, never a second reward for producing output.</summary>
public sealed record ForecastProductiveLearning(string Skill, EquatableArray<SkillXpEvent> Callbacks, SkillProjection Before,
    SkillProjection After, double ObservedWorkTicks, double WorkTicks, string Provenance)
{
    public double WorkAmount { get; init; }
    public EquatableArray<ForecastWorkSegment> WorkSegments { get; init; }
}
public sealed record ForecastWorkSegment(int Start, int End, double WorkPerTick, int CallbackInterval);
public sealed record ForecastProductiveDuration(Fact<double> WorkTicks, ForecastProductiveLearning? Evidence, string Reason);

public static class ForecastProductiveWorkPolicy
{
    public static ForecastProductiveDuration ForecastProductiveWork(ForecastActivityOption option,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        if (option.SkillWork is not { } work)
            return option.TendLearning is { } tend ? Tend(option, tend, assigned, start) : new(option.WorkTicks, null, "learning_effect_unassessed");
        ForecastProductiveDuration Unknown(string reason) => new(default, null, reason);
        var mechanics = work.Learning;
        if (!double.IsFinite(start) || start < 0 || start > GameTime.TicksPerDay
            || !work.Recipe.RawXpPerWorkTick.TryGetValue(out var raw) || !double.IsFinite(raw) || raw < 0
            || !work.WorkAmount.TryGetValue(out var remaining) || !double.IsFinite(remaining) || remaining <= 0
            || ForecastSkillPolicy.MechanicsGap(mechanics) is not null
            || work.CallbackIntervalTicks <= 0 || work.CallbackIntervalTicks > 15
            || work.CallbackPhase < 0 || work.CallbackPhase >= work.CallbackIntervalTicks
            || work.Rates.Select(r => r.StoredLevel).Distinct().Count() != work.Rates.Count)
            return Unknown("productive_mechanics_unknown");
        var events = Prior(assigned, option.Pawn, work.Recipe.Skill);
        var tick = (int)Math.Ceiling(start);
        if (events.Any(e => e.LastTick > tick)) return Unknown("learning_callback_after_work_start");
        var before = ForecastSkillPolicy.ForecastSkill(mechanics, events, tick);
        if (!before.StoredLevel.IsKnown) return Unknown(string.Join(",", before.Findings));
        var cursor = new ForecastSkillCursor(mechanics);
        foreach (var e in events)
            for (var i = 0; i < e.Count; i++)
            {
                var at = e.Tick + i * e.Interval;
                cursor.Advance(e.BeforeDecay ? at - 1 : at); cursor.Learn(e); cursor.Advance(at);
            }
        cursor.Advance(tick);
        var callbacks = new List<SkillXpEvent>();
        var segments = new List<ForecastWorkSegment>();
        var worked = 0; var amount = 0d;
        var limit = mechanics.NextMidnightReset.IsKnown ? GameTime.TicksPerDay : Math.Min(GameTime.TicksPerDay, mechanics.ValidForTicks.Value - 1);
        var fixedTicks = work.FixedDuration && option.WorkTicks.IsKnown ? (int)Math.Ceiling(option.WorkTicks.Value) : 0;
        while (work.FixedDuration ? worked < fixedTicks : remaining > 1e-9)
        {
            var delta = work.Recipe.DuringWork ? work.CallbackIntervalTicks - (tick - work.CallbackPhase + work.CallbackIntervalTicks) % work.CallbackIntervalTicks : 1;
            if (tick + delta > limit) return Unknown("productive_learning_window_expired");
            var at = tick + delta;
            // Native DriverTickInterval credits the full observed delta even when this toil began between callbacks.
            var workDelta = work.Recipe.DuringWork ? work.CallbackIntervalTicks : delta;
            cursor.Advance(at - 1);
            var callback = new SkillXpEvent(option.Activity + "/" + at, at, Known(raw * workDelta), SkillXpPath.NormalTracker, BeforeDecay: true);
            if (work.Recipe.DuringWork && work.LearnBeforeWork) cursor.Learn(callback);
            var rate = work.Rates.SingleOrDefault(r => r.StoredLevel == cursor.StoredLevel)?.WorkPerTick ?? default;
            if (!rate.TryGetValue(out var speed) || !double.IsFinite(speed) || speed <= 0) return Unknown("skill_work_rate_unknown");
            var progress = speed * workDelta;
            if (work.WorkQuantum > 0)
                progress = Math.Min(progress, (Math.Floor((amount + 1e-9) / work.WorkQuantum) + 1) * work.WorkQuantum - amount);
            var effectiveSpeed = progress / delta;
            if (segments.Count > 0 && segments[^1].WorkPerTick == effectiveSpeed && segments[^1].CallbackInterval == delta)
                segments[^1] = segments[^1] with { End = at };
            else segments.Add(new(tick, at, effectiveSpeed, delta));
            amount += progress; remaining -= progress; worked += delta; tick = at;
            if (work.Recipe.DuringWork)
            {
                if (!work.LearnBeforeWork) cursor.Learn(callback);
                // Repeated callbacks retain native saturation granularity without one evidence row per tick.
                if (callbacks.Count > 0 && callbacks[^1] is { } last && last.RawXp == callback.RawXp
                    && (last.Count == 1 || last.Interval == delta) && last.LastTick + delta == at)
                    callbacks[^1] = last with { Count = last.Count + 1, Interval = delta };
                else callbacks.Add(callback);
            }
            cursor.Advance(tick);
        }
        if (!work.Recipe.DuringWork)
        {
            var callback = ForecastSkillPolicy.RecipeCompletionLearning(work.Recipe, option.Activity, tick, worked, true);
            cursor.Learn(callback); callbacks.Add(callback);
        }
        var after = cursor.Snapshot();
        if (!after.StoredLevel.IsKnown) return Unknown(string.Join(",", after.Findings));
        var duration = tick - start;
        return new(Known(duration), new(work.Recipe.Skill, EquatableArray.From(callbacks), before, after,
            option.WorkTicks.IsKnown ? option.WorkTicks.Value : duration, duration, work.Provenance) { WorkAmount = amount, WorkSegments = EquatableArray.From(segments) }, "conditional_productive_learning");
    }

    /// <summary>First native work callback that reaches the requested conserved work units.</summary>
    public static Fact<double> CompletionTick(ForecastProductiveLearning learning, double units)
    {
        if (!double.IsFinite(units) || units <= 0) return default;
        foreach (var segment in learning.WorkSegments)
        {
            var amount = (segment.End - segment.Start) * segment.WorkPerTick;
            if (units <= amount + 1e-9)
                return Known((double)Math.Min(segment.End, segment.Start + Math.Ceiling(units / segment.WorkPerTick / segment.CallbackInterval) * segment.CallbackInterval));
            units -= amount;
        }
        return default;
    }

    private static ForecastProductiveDuration Tend(ForecastActivityOption option, TendLearning tend,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        if (!option.WorkTicks.TryGetValue(out var work) || !double.IsFinite(work) || work < 0
            || !double.IsFinite(start) || start < 0 || start + work > GameTime.TicksPerDay)
            return new(option.WorkTicks, null, "tend_learning_outside_window");
        var tick = (int)Math.Ceiling(start);
        var events = Prior(assigned, option.Pawn, tend.Skill);
        if (events.Any(e => e.LastTick > tick)) return new(option.WorkTicks, null, "learning_callback_after_work_start");
        var before = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, tick);
        var end = (int)Math.Ceiling(start + work);
        var callback = ForecastSkillPolicy.TendCompletionLearning(tend, option.Activity, end);
        events.Add(callback);
        var after = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, end);
        if (!before.StoredLevel.IsKnown || !after.StoredLevel.IsKnown)
            return new(option.WorkTicks, null, string.Join(",", before.Findings.Concat(after.Findings).Distinct()));
        return new(option.WorkTicks, new(tend.Skill, [callback], before, after, work, work, tend.Provenance), "conditional_tend_learning");
    }
    private static List<SkillXpEvent> Prior(IReadOnlyList<ForecastActivityAllocation> assigned, string pawn, string skill)
        => assigned.Where(a => a.Pawn == pawn && a.Learning?.Skill == skill)
            .SelectMany(a => a.Learning!.Callbacks).OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
}
