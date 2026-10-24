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
/// <summary>An option's work duration from its start: timed, with the learning it carries when assessed, or unprojectable.</summary>
public abstract record ForecastProductiveDuration(string Reason)
{
    /// <summary>The work takes <paramref name="WorkTicks"/>; <paramref name="Evidence"/> is its learning, null when none was assessed.</summary>
    public sealed record Timed(double WorkTicks, ForecastProductiveLearning? Evidence, string Reason) : ForecastProductiveDuration(Reason);

    /// <summary>The native mechanics cannot time the work; the option is not planned.</summary>
    public sealed record Unprojectable(string Reason) : ForecastProductiveDuration(Reason);
}

public static class ForecastProductiveWorkPolicy
{
    public static ForecastProductiveDuration ForecastProductiveWork(ForecastActivityOption option,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start, double? stopAt = null)
    {
        if (option.SkillWork is not { } work)
            return option.TendLearning is { } tend ? Tend(option, tend, assigned, start)
                : new ForecastProductiveDuration.Timed(option.WorkTicks, null, "learning_effect_unassessed");
        static ForecastProductiveDuration Untimed(string reason) => new ForecastProductiveDuration.Unprojectable(reason);
        var mechanics = work.Learning;
        if (!double.IsFinite(start) || start < 0 || start > GameTime.TicksPerDay
            || !double.IsFinite(work.PreparationTicks) || work.PreparationTicks < 0
            || !double.IsFinite(work.WorkAmount) || work.WorkAmount <= 0
            || ForecastSkillPolicy.MechanicsGap(mechanics) is not null
            || work.CallbackIntervalTicks <= 0 || work.CallbackIntervalTicks > 15
            || work.CallbackElapsedTicks < 0 || work.CallbackElapsedTicks >= 15
            || work.CallbackPhase < 0 || work.CallbackPhase >= work.CallbackIntervalTicks
            || work.Rates.Select(r => r.StoredLevel).Distinct().Count() != work.Rates.Count)
            return Untimed("productive_mechanics_unknown");
        var stages = work.Stages.Count == 0
            ? new[] { new ProductiveWorkStage(work.WorkAmount, 1, work.Recipe.RawXpPerWorkTick, work.WorkQuantum) }
            : work.Stages.ToArray();
        if (stages.Any(s => !double.IsFinite(s.WorkAmount) || s.WorkAmount <= 0 || !double.IsFinite(s.RateFactor) || s.RateFactor <= 0
            || !double.IsFinite(s.RawXpPerWorkTick) || s.RawXpPerWorkTick < 0
            || !double.IsFinite(s.WorkQuantum) || s.WorkQuantum < 0 || s.HoldRateUntilQuantum && s.WorkQuantum <= 0))
            return Untimed("productive_stage_unknown");
        var events = Prior(assigned, option.Pawn, work.Recipe.Skill);
        var tick = (int)Math.Ceiling(start + work.PreparationTicks);
        var workStart = tick;
        if (events.Any(e => e.LastTick > tick)) return Untimed("learning_callback_after_work_start");
        var beforeForecast = ForecastSkillPolicy.ForecastSkill(mechanics, events, tick);
        if (beforeForecast is not SkillForecast.Projected { Projection: var before }) return Untimed(string.Join(",", ForecastSkillPolicy.FindingsOf(beforeForecast)));
        var cursor = new ForecastSkillCursor(mechanics);
        foreach (var e in events)
            for (var i = 0; i < e.Count; i++)
            {
                var at = e.Tick + i * e.Interval;
                cursor.Advance(e.BeforeDecay ? at - 1 : at); cursor.Learn(e); cursor.Advance(at);
            }
        cursor.Advance(tick);
        var callbacks = new List<SkillXpEvent>(); var segments = new List<ForecastWorkSegment>();
        var creditedTicks = 0; var amount = 0d;
        var clock = new TickIntervalClock(work.CallbackIntervalTicks, work.CallbackPhase, work.CallbackElapsedTicks);
        var runFirst = 0; var runLast = 0; var runCount = 0; var runInterval = 0; var runRaw = 0d;
        var segmentStart = tick; var segmentEnd = tick; var segmentRate = 0d; var segmentCadence = 0;
        var fixedTicks = work.FixedDuration ? (int)Math.Ceiling(option.WorkTicks - work.PreparationTicks) : 0;
        // The work a tick at the cursor's stored level; null when the read has no rate at that level.
        double? Rate(ProductiveWorkStage stage)
        {
            var level = cursor.StoredLevel;
            var rate = level < work.Rates.Count && work.Rates[level].StoredLevel == level ? work.Rates[level]
                : work.Rates.FirstOrDefault(r => r.StoredLevel == level);
            return rate is null ? null : rate.WorkPerTick * stage.RateFactor;
        }
        for (var stageIndex = 0; stageIndex < stages.Length; stageIndex++)
        {
            var stage = stages[stageIndex];
            var done = 0d;
            var heldRate = Rate(stage);
            while (stage.WorkAmount - done > 1e-9 && (!work.FixedDuration || tick - workStart < fixedTicks))
            {
                var (at, delta) = clock.Next(tick);
                var elapsed = at - tick;
                if (stopAt is { } cutoff && at > cutoff) break;
                if (at > GameTime.TicksPerDay) return Untimed("productive_learning_window_expired");
                cursor.Advance(at - 1);
                var raw = stage.RawXpPerWorkTick * delta;
                if (work.Recipe.DuringWork && work.LearnBeforeWork) cursor.Learn(raw, SkillXpPath.NormalTracker);
                var rate = stage.HoldRateUntilQuantum ? heldRate : Rate(stage);
                if (rate is not { } speed || !double.IsFinite(speed) || speed <= 0) return Untimed("skill_work_rate_unread_or_zero");
                var progress = speed * delta;
                if (stage.WorkQuantum > 0)
                    progress = Math.Min(progress, (Math.Floor((done + 1e-9) / stage.WorkQuantum) + 1) * stage.WorkQuantum - done);
                progress = Math.Min(progress, stage.WorkAmount - done);
                var effectiveSpeed = progress / elapsed;
                if (segmentEnd > segmentStart && (segmentRate != effectiveSpeed || segmentCadence != elapsed)) FlushSegment();
                if (segmentEnd == segmentStart) { segmentStart = tick; segmentRate = effectiveSpeed; segmentCadence = elapsed; }
                segmentEnd = at;
                amount += progress; done += progress; creditedTicks += delta; tick = at;
                if (work.Recipe.DuringWork)
                {
                    if (!work.LearnBeforeWork) cursor.Learn(raw, SkillXpPath.NormalTracker);
                    if (runCount > 0 && (runRaw != raw || runCount > 1 && tick - runLast != runInterval)) FlushCallbacks();
                    if (runCount == 0) { runFirst = tick; runRaw = raw; }
                    else runInterval = tick - runLast;
                    runLast = tick; runCount++;
                }
                if (stage.HoldRateUntilQuantum && Math.Abs(done / stage.WorkQuantum - Math.Round(done / stage.WorkQuantum)) < 1e-9)
                    heldRate = Rate(stage); // JobDriver_Mine resets the next pick timer after learning, before SkillsTickInterval.
                if (!work.Recipe.DuringWork && stageIndex == stages.Length - 1 && stage.WorkAmount - done <= 1e-9)
                {
                    var callback = ForecastSkillPolicy.RecipeCompletionLearning(work.Recipe, option.Activity, tick, creditedTicks, true) with { BeforeDecay = true };
                    cursor.Learn(callback); callbacks.Add(callback);
                }
                cursor.Advance(tick);
            }
        }
        FlushCallbacks(); FlushSegment();
        if (cursor.Snapshot(tick) is not SkillForecast.Projected { Projection: var after }) return Untimed("learning_arithmetic_overflow");
        var duration = tick - start;
        return new ForecastProductiveDuration.Timed(duration, new(work.Recipe.Skill, EquatableArray.From(callbacks), before, after,
            option.WorkTicks, duration, work.Provenance) { WorkAmount = amount, WorkSegments = EquatableArray.From(segments) }, "conditional_productive_learning");

        void FlushCallbacks()
        {
            if (runCount == 0) return;
            callbacks.Add(new(option.Activity + "/callback/" + runFirst, runFirst, runRaw, SkillXpPath.NormalTracker,
                BeforeDecay: true, Count: runCount, Interval: runCount > 1 ? runInterval : 0));
            runCount = 0; runInterval = 0;
        }
        void FlushSegment()
        {
            if (segmentEnd > segmentStart) segments.Add(new(segmentStart, segmentEnd, segmentRate, segmentCadence));
            segmentStart = segmentEnd;
        }
    }

    /// <summary>First native work callback that reaches the requested conserved work units; null when the work never reaches them.</summary>
    public static double? CompletionTick(ForecastProductiveLearning learning, double units)
    {
        if (!double.IsFinite(units) || units <= 0) return null;
        foreach (var segment in learning.WorkSegments)
        {
            var amount = (segment.End - segment.Start) * segment.WorkPerTick;
            if (units <= amount + 1e-9)
                return Math.Min(segment.End, segment.Start + Math.Ceiling(units / segment.WorkPerTick / segment.CallbackInterval) * segment.CallbackInterval);
            units -= amount;
        }
        return null;
    }

    private static ForecastProductiveDuration Tend(ForecastActivityOption option, TendLearning tend,
        IReadOnlyList<ForecastActivityAllocation> assigned, double start)
    {
        var work = option.WorkTicks;
        ForecastProductiveDuration Unassessed(string reason) => new ForecastProductiveDuration.Timed(work, null, reason);
        if (!double.IsFinite(work) || work < 0 || !double.IsFinite(start) || start < 0 || start + work > GameTime.TicksPerDay)
            return Unassessed("tend_learning_outside_window");
        var tick = (int)Math.Ceiling(start);
        var events = Prior(assigned, option.Pawn, tend.Skill);
        if (events.Any(e => e.LastTick > tick)) return Unassessed("learning_callback_after_work_start");
        var beforeForecast = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, tick);
        var end = (int)Math.Ceiling(start + work);
        var callback = ForecastSkillPolicy.TendCompletionLearning(tend, option.Activity, end) with { BeforeDecay = true };
        events.Add(callback);
        var afterForecast = ForecastSkillPolicy.ForecastSkill(tend.Learning, events, end);
        if (beforeForecast is not SkillForecast.Projected { Projection: var before } || afterForecast is not SkillForecast.Projected { Projection: var after })
            return Unassessed(string.Join(",", ForecastSkillPolicy.FindingsOf(beforeForecast).Concat(ForecastSkillPolicy.FindingsOf(afterForecast)).Distinct()));
        return new ForecastProductiveDuration.Timed(work, new(tend.Skill, [callback], before, after, work, work, tend.Provenance), "conditional_tend_learning");
    }

    private static List<SkillXpEvent> Prior(IReadOnlyList<ForecastActivityAllocation> assigned, string pawn, string skill)
        => assigned.Where(a => a.Pawn == pawn && a.Learning?.Skill == skill)
            .SelectMany(a => a.Learning!.Callbacks).OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
}
