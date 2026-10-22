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
        IReadOnlyList<ForecastActivityAllocation> assigned, double start, double? stopAt = null)
    {
        if (option.SkillWork is not { } work)
            return option.TendLearning is { } tend ? Tend(option, tend, assigned, start) : new(option.WorkTicks, null, "learning_effect_unassessed");
        ForecastProductiveDuration Unknown(string reason) => new(default, null, reason);
        var mechanics = work.Learning;
        if (!double.IsFinite(start) || start < 0 || start > GameTime.TicksPerDay
            || !double.IsFinite(work.PreparationTicks) || work.PreparationTicks < 0
            || !work.WorkAmount.TryGetValue(out var requested) || !double.IsFinite(requested) || requested <= 0
            || ForecastSkillPolicy.MechanicsGap(mechanics) is not null
            || work.CallbackIntervalTicks <= 0 || work.CallbackIntervalTicks > 15
            || work.CallbackElapsedTicks < 0 || work.CallbackElapsedTicks >= 15
            || work.CallbackPhase < 0 || work.CallbackPhase >= work.CallbackIntervalTicks
            || work.Rates.Select(r => r.StoredLevel).Distinct().Count() != work.Rates.Count)
            return Unknown("productive_mechanics_unknown");
        var stages = work.Stages.Count == 0
            ? new[] { new ProductiveWorkStage(requested, 1, work.Recipe.RawXpPerWorkTick, work.WorkQuantum) }
            : work.Stages.ToArray();
        if (stages.Any(s => !double.IsFinite(s.WorkAmount) || s.WorkAmount <= 0 || !double.IsFinite(s.RateFactor) || s.RateFactor <= 0
            || !s.RawXpPerWorkTick.IsKnown || !double.IsFinite(s.RawXpPerWorkTick.Value) || s.RawXpPerWorkTick.Value < 0
            || !double.IsFinite(s.WorkQuantum) || s.WorkQuantum < 0 || s.HoldRateUntilQuantum && s.WorkQuantum <= 0))
            return Unknown("productive_stage_unknown");
        var events = Prior(assigned, option.Pawn, work.Recipe.Skill);
        var tick = (int)Math.Ceiling(start + work.PreparationTicks);
        var workStart = tick;
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
        var callbacks = new List<SkillXpEvent>(); var segments = new List<ForecastWorkSegment>();
        var creditedTicks = 0; var amount = 0d;
        var clock = new TickIntervalClock(work.CallbackIntervalTicks, work.CallbackPhase, work.CallbackElapsedTicks);
        var runFirst = 0; var runLast = 0; var runCount = 0; var runInterval = 0; var runRaw = 0d;
        var segmentStart = tick; var segmentEnd = tick; var segmentRate = 0d; var segmentCadence = 0;
        var limit = mechanics.NextMidnightReset.IsKnown ? GameTime.TicksPerDay : Math.Min(GameTime.TicksPerDay, mechanics.ValidForTicks.Value - 1);
        var fixedTicks = work.FixedDuration && option.WorkTicks.IsKnown ? (int)Math.Ceiling(option.WorkTicks.Value - work.PreparationTicks) : 0;
        Fact<double> Rate(ProductiveWorkStage stage)
        {
            var level = cursor.StoredLevel;
            var rate = level < work.Rates.Count && work.Rates[level].StoredLevel == level ? work.Rates[level].WorkPerTick
                : work.Rates.FirstOrDefault(r => r.StoredLevel == level)?.WorkPerTick ?? default;
            return rate.IsKnown ? Known(rate.Value * stage.RateFactor) : default;
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
                if (at > limit) return Unknown("productive_learning_window_expired");
                cursor.Advance(at - 1);
                var raw = stage.RawXpPerWorkTick.Value * delta;
                if (work.Recipe.DuringWork && work.LearnBeforeWork) cursor.Learn(raw, SkillXpPath.NormalTracker);
                var rate = stage.HoldRateUntilQuantum ? heldRate : Rate(stage);
                if (!rate.TryGetValue(out var speed) || !double.IsFinite(speed) || speed <= 0) return Unknown("skill_work_rate_unknown");
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
        var after = cursor.Snapshot() with { TerminalLearning = cursor.Rebase(tick) };
        if (!after.StoredLevel.IsKnown) return Unknown(string.Join(",", after.Findings));
        var duration = tick - start;
        return new(Known(duration), new(work.Recipe.Skill, EquatableArray.From(callbacks), before, after,
            option.WorkTicks.IsKnown ? option.WorkTicks.Value : duration, duration, work.Provenance) { WorkAmount = amount, WorkSegments = EquatableArray.From(segments) }, "conditional_productive_learning");

        void FlushCallbacks()
        {
            if (runCount == 0) return;
            callbacks.Add(new(option.Activity + "/callback/" + runFirst, runFirst, Known(runRaw), SkillXpPath.NormalTracker,
                BeforeDecay: true, Count: runCount, Interval: runCount > 1 ? runInterval : 0));
            runCount = 0; runInterval = 0;
        }
        void FlushSegment()
        {
            if (segmentEnd > segmentStart) segments.Add(new(segmentStart, segmentEnd, segmentRate, segmentCadence));
            segmentStart = segmentEnd;
        }
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
        var callback = ForecastSkillPolicy.TendCompletionLearning(tend, option.Activity, end) with { BeforeDecay = true };
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
