using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public enum SkillXpPath { NormalRecord, NormalTracker, Range }
/// <summary>One actual learning callback, not an additional reward for output or skill thresholds.
/// Events must be supplied once, in native order. An interrupted completion-only recipe has no event.</summary>
public sealed record SkillXpEvent(string Id, int Tick, Fact<double> RawXp, SkillXpPath Path, bool Practice = false, bool BeforeDecay = false, int Count = 1, int Interval = 0)
{
    public long LastTick => (long)Tick + (long)(Count - 1) * Interval;
}
public sealed record SkillProjection(Fact<int> Level, Fact<int> StoredLevel, Fact<double> Xp,
    Fact<double> AppliedGain, Fact<double> DecayLoss, EquatableArray<string> Findings);

public static class ForecastSkillPolicy
{
    /// <summary>Bounded conditional projection of supplied learning callbacks. It predicts neither job success,
    /// attack cadence nor training safety. Native observations alone establish attained capability.</summary>
    public static SkillProjection ForecastSkill(SkillLearning? observed, IReadOnlyList<SkillXpEvent> events, int horizon)
        => Project(observed, events, horizon, false);

    /// <summary>Terminal stock only: after the callback validity cut, native passive decay/settlement can continue.
    /// Local/global midnight resets callback saturation/range budgets, not stored XP or the native decay table.
    /// No callback at or after the cut is accepted, and the ordinary learning API retains its original bound.</summary>
    internal static SkillProjection ForecastTerminalSkill(SkillLearning? observed, IReadOnlyList<SkillXpEvent> events, int horizon)
        => Project(observed, events, horizon, true);

    private static SkillProjection Project(SkillLearning? observed, IReadOnlyList<SkillXpEvent> events, int horizon, bool passiveTail)
    {
        SkillProjection Unknown(string why) => new(default, default, default, default, default, [why]);
        if (horizon < 0 || horizon > GameTime.TicksPerDay || events.Count > 4096)
            return Unknown("learning_horizon_or_event_bound");
        if (MechanicsGap(observed) is { } gap) return Unknown(gap);
        var s = observed!;
        if (horizon >= s.ValidForTicks.Value && !s.NextMidnightReset.IsKnown && (!passiveTail || events.Any(e => e.LastTick >= s.ValidForTicks.Value)))
            return Unknown("learning_mechanics_outside_supported_window");
        if (events.Any(e => string.IsNullOrEmpty(e.Id) || e.Tick < 0 || e.LastTick > horizon || e.Count <= 0 || e.Count > GameTime.TicksPerDay || e.Interval < 0 || e.Count > 1 && e.Interval == 0 || !e.RawXp.IsKnown
                || !double.IsFinite(e.RawXp.Value) || e.RawXp.Value < 0 || !Enum.IsDefined(e.Path))
            || events.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != events.Count
            || events.Zip(events.Skip(1)).Any(pair => pair.First.LastTick > pair.Second.Tick))
            return Unknown("learning_events_unknown_duplicate_or_unordered");
        if (events.Any(e => e.Path == SkillXpPath.Range && e.LastTick >= s.ValidForTicks.Value)) return Unknown("learning_mechanics_outside_supported_window");
        if (events.Any(e => e.Path == SkillXpPath.Range) && (!s.RangeAppliedBudget.IsKnown
            || !double.IsFinite(s.RangeAppliedBudget.Value) || s.RangeAppliedBudget.Value < 0))
            return Unknown("range_budget_unknown");
        if (events.Any(e => e.Practice || e.Path == SkillXpPath.Range)
            && (!s.PracticeEligible.IsKnown || !s.PracticeCeiling.IsKnown))
            return Unknown("practice_eligibility_unknown");
        var cursor = new ForecastSkillCursor(s);
        foreach (var e in events)
            for (var i = 0; i < e.Count; i++)
            {
                var tick = e.Tick + i * e.Interval;
                cursor.Advance(e.BeforeDecay ? tick - 1 : tick);
                if ((e.Practice || e.Path == SkillXpPath.Range) && !s.PracticeEligible.Value
                    && Math.Clamp((long)s.StoredLevel.Value + s.Aptitude.Value, 0, s.Levels.Count - 1) >= s.PracticeCeiling.Value
                    && cursor.Effective < s.PracticeCeiling.Value) return Unknown("practice_eligibility_requires_refresh");
                cursor.Learn(e);
                cursor.Advance(tick);
            }
        cursor.Advance(horizon);
        return cursor.Snapshot();
    }
    /// <summary>The first reason the mechanics cannot be projected at all, or null: an unread fact
    /// (<c>learning_mechanics_unknown</c>) or a value outside the supported window. The horizon bound is the caller's.</summary>
    internal static string? MechanicsGap(SkillLearning? observed)
    {
        if (observed is not { } s || !s.Disabled.IsKnown || !s.TrackerAllowsLearning.IsKnown
            || !s.StoredLevel.IsKnown || !s.Aptitude.IsKnown || !s.Xp.IsKnown || !s.XpSinceMidnight.IsKnown
            || !s.DirectFactor.IsKnown || !s.UnsaturatedFactor.IsKnown || !s.SaturationThreshold.IsKnown
            || !s.SaturatedFactor.IsKnown || !s.LossThreshold.IsKnown || !s.DecayInterval.IsKnown
            || !s.NextDecay.IsKnown || !s.ValidForTicks.IsKnown)
            return "learning_mechanics_unknown";
        if (s.TrackerIntervalTicks <= 0 || s.TrackerIntervalTicks > 15 || s.TrackerPhase < 0 || s.TrackerPhase >= s.TrackerIntervalTicks || s.NextMidnightReset.IsKnown && (s.NextMidnightReset.Value <= 0 || s.NextMidnightReset.Value > GameTime.TicksPerDay + s.DecayInterval.Value) || s.DecayInterval.Value <= 0 || s.DecayInterval.Value > GameTime.TicksPerDay
            || s.NextDecay.Value <= 0 || s.NextDecay.Value > s.DecayInterval.Value
            || s.Levels.Count == 0 || s.StoredLevel.Value < 0 || s.StoredLevel.Value >= s.Levels.Count
            || s.Levels.Where((l, i) => l.Level != i || !double.IsFinite(l.RequiredXp) || l.RequiredXp <= 0
                || !double.IsFinite(l.DecayXp) || l.DecayXp < 0).Any()
            || !double.IsFinite(s.Xp.Value) || !double.IsFinite(s.XpSinceMidnight.Value)
            || !double.IsFinite(s.DirectFactor.Value) || s.DirectFactor.Value < 0
            || !double.IsFinite(s.UnsaturatedFactor.Value) || s.UnsaturatedFactor.Value < 0
            || !double.IsFinite(s.SaturatedFactor.Value) || s.SaturatedFactor.Value < 0
            || !double.IsFinite(s.SaturationThreshold.Value) || !double.IsFinite(s.LossThreshold.Value) || s.LossThreshold.Value >= 0)
            return "learning_mechanics_outside_supported_window";
        return null;
    }

    /// <summary>The native level rule after XP moved (<c>SkillRecord.Learn</c>): level up at each <c>RequiredXp</c>, the top level
    /// capped below its own; level down at or under the loss threshold, XP reset at level 0. The tier-2 skill stock (#3634) shares it.</summary>
    internal static void Settle(SkillLearning s, ref int level, ref double xp)
    {
        while (xp >= s.Levels[level].RequiredXp && level < s.Levels.Count - 1)
        { xp -= s.Levels[level].RequiredXp; level++; }
        if (level == s.Levels.Count - 1) xp = Math.Min(xp, s.Levels[level].RequiredXp - 1);
        while (xp <= s.LossThreshold.Value && level > 0)
        { level--; xp += s.Levels[level].RequiredXp; if (level == 0) xp = 0; }
    }

    /// <summary>One supported recipe completion callback, surgery included (a failed surgery pays too). workTicks are the
    /// ticks spent on recipe work, as native <c>ticksSpentDoingRecipeWork</c>, not the work amount. UFT work must supply
    /// individual native work callbacks so saturation is applied at the same granularity; no second completion credit is allowed.</summary>
    public static SkillXpEvent RecipeCompletionLearning(RecipeLearning recipe, string id, int tick, double workTicks, bool completed)
        => new(id, tick, recipe.DuringWork || !recipe.RawXpPerWorkTick.IsKnown || !double.IsFinite(workTicks) || workTicks < 0
            ? default : Known(completed ? recipe.RawXpPerWorkTick.Value * workTicks : 0), SkillXpPath.NormalRecord);

    /// <summary>One completed tend's callback: the native raw XP once, through the skill tracker, at the treatment's completion.</summary>
    public static SkillXpEvent TendCompletionLearning(TendLearning tend, string id, int tick)
        => new(id, tick, tend.RawXp, SkillXpPath.NormalTracker);
}
