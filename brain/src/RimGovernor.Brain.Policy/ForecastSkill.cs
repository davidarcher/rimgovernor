using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public enum SkillXpPath { NormalRecord, NormalTracker, Range }
/// <summary>One actual learning callback, not an additional reward for output or skill thresholds.
/// Events must be supplied once, in native order. An interrupted completion-only recipe has no event.</summary>
public sealed record SkillXpEvent(string Id, int Tick, double RawXp, SkillXpPath Path, bool Practice = false, bool BeforeDecay = false, int Count = 1, int Interval = 0)
{
    public long LastTick => (long)Tick + (long)(Count - 1) * Interval;
}
/// <summary>A skill's native learning at the end of a projection: its effective and stored level, XP, the XP the callbacks
/// applied and decay took, and the mechanics rebased to that tick.</summary>
public sealed record SkillProjection(int Level, int StoredLevel, double Xp, double AppliedGain, double DecayLoss, SkillLearning TerminalLearning);

/// <summary>A projection of supplied learning callbacks (#3783): projected, or not projectable for the named findings (an
/// event, horizon or mechanic outside the supported window), so the forecast does not consider it.</summary>
public abstract record SkillForecast
{
    private protected SkillForecast() { }

    public sealed record Projected(SkillProjection Projection) : SkillForecast;

    public sealed record Unprojectable(EquatableArray<string> Findings) : SkillForecast;
}

public static class ForecastSkillPolicy
{
    /// <summary>Bounded conditional projection of supplied learning callbacks. It predicts neither job success,
    /// attack cadence nor training safety. Native observations alone establish attained capability. Local/global midnight
    /// resets callback saturation/range budgets, not stored XP or the native decay table.</summary>
    public static SkillForecast ForecastSkill(SkillLearning? observed, IReadOnlyList<SkillXpEvent> events, int horizon)
    {
        static SkillForecast Unprojectable(string why) => new SkillForecast.Unprojectable([why]);
        if (horizon < 0 || horizon > GameTime.TicksPerDay || events.Count > 4096)
            return Unprojectable("learning_horizon_or_event_bound");
        if (MechanicsGap(observed) is { } gap) return Unprojectable(gap);
        var s = observed!;
        if (events.Any(e => string.IsNullOrEmpty(e.Id) || e.Tick < 0 || e.LastTick > horizon || e.Count <= 0 || e.Count > GameTime.TicksPerDay || e.Interval < 0 || e.Count > 1 && e.Interval == 0
                || !double.IsFinite(e.RawXp) || e.RawXp < 0 || !Enum.IsDefined(e.Path))
            || events.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != events.Count
            || events.Zip(events.Skip(1)).Any(pair => pair.First.LastTick > pair.Second.Tick))
            return Unprojectable("learning_events_invalid_duplicate_or_unordered");
        if (events.Any(e => e.Path == SkillXpPath.Range && e.LastTick >= s.ValidForTicks)) return Unprojectable("learning_mechanics_outside_supported_window");
        if (events.Any(e => e.Practice || e.Path == SkillXpPath.Range) && s.Practice is not SkillPractice.Drill)
            return Unprojectable("practice_event_on_unpractised_skill");
        if (events.Any(e => e.Path == SkillXpPath.Range)
            && (s.Practice is not SkillPractice.Range range || !double.IsFinite(range.AppliedBudget) || range.AppliedBudget < 0))
            return Unprojectable("range_event_without_range_budget");
        var cursor = new ForecastSkillCursor(s);
        foreach (var e in events)
            for (var i = 0; i < e.Count; i++)
            {
                var tick = e.Tick + i * e.Interval;
                cursor.Advance(e.BeforeDecay ? tick - 1 : tick);
                if ((e.Practice || e.Path == SkillXpPath.Range) && s.Practice is SkillPractice.Drill { Eligible: false } drill
                    && Math.Clamp((long)s.StoredLevel + s.Aptitude, 0, s.Levels.Count - 1) >= drill.Ceiling
                    && cursor.Effective < drill.Ceiling) return Unprojectable("practice_eligibility_requires_refresh");
                cursor.Learn(e);
                cursor.Advance(tick);
            }
        cursor.Advance(horizon);
        return cursor.Snapshot(horizon);
    }

    /// <summary>The findings that left a forecast unprojectable; none once projected.</summary>
    internal static EquatableArray<string> FindingsOf(SkillForecast forecast)
        => forecast is SkillForecast.Unprojectable unprojectable ? unprojectable.Findings : [];

    /// <summary>The first reason the mechanics cannot be projected at all, or null: no mechanics read for the skill
    /// (<c>learning_mechanics_unread</c>) or a value outside the supported window. The horizon bound is the caller's.</summary>
    internal static string? MechanicsGap(SkillLearning? observed)
    {
        if (observed is not { } s) return "learning_mechanics_unread";
        if (s.TrackerIntervalTicks <= 0 || s.TrackerIntervalTicks > 15 || s.TrackerPhase < 0 || s.TrackerPhase >= s.TrackerIntervalTicks || s.NextMidnightReset < 0 || s.NextMidnightReset > GameTime.TicksPerDay + s.DecayInterval || s.DecayInterval <= 0 || s.DecayInterval > GameTime.TicksPerDay
            || s.TrackerElapsedTicks < 0 || s.TrackerElapsedTicks >= 15 || s.NextDecay <= -15 || s.NextDecay > s.DecayInterval
            || s.NextDecay <= -s.TrackerElapsedTicks
            || s.Levels.Count == 0 || s.StoredLevel < 0 || s.StoredLevel >= s.Levels.Count
            || s.Levels.Where((l, i) => l.Level != i || !double.IsFinite(l.RequiredXp) || l.RequiredXp <= 0
                || !double.IsFinite(l.DecayXp) || l.DecayXp < 0).Any()
            || !double.IsFinite(s.Xp) || !double.IsFinite(s.XpSinceMidnight)
            || !double.IsFinite(s.DirectFactor) || s.DirectFactor < 0
            || !double.IsFinite(s.UnsaturatedFactor) || s.UnsaturatedFactor < 0
            || !double.IsFinite(s.SaturatedFactor) || s.SaturatedFactor < 0
            || !double.IsFinite(s.SaturationThreshold) || !double.IsFinite(s.LossThreshold) || s.LossThreshold >= 0)
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
        while (xp <= s.LossThreshold && level > 0)
        { level--; xp += s.Levels[level].RequiredXp; if (level == 0) xp = 0; }
    }

    /// <summary>One supported recipe completion callback, surgery included (a failed surgery pays too). workTicks are the
    /// ticks spent on recipe work, as native <c>ticksSpentDoingRecipeWork</c>, not the work amount. UFT work must supply
    /// individual native work callbacks so saturation is applied at the same granularity; no second completion credit is allowed,
    /// so a during-work recipe has no completion callback.</summary>
    public static SkillXpEvent RecipeCompletionLearning(RecipeLearning recipe, string id, int tick, double workTicks, bool completed)
    {
        if (recipe.DuringWork) throw new ArgumentException("a during-work recipe pays no completion learning", nameof(recipe));
        if (!double.IsFinite(workTicks) || workTicks < 0) throw new ArgumentOutOfRangeException(nameof(workTicks), workTicks, "recipe work ticks");
        return new(id, tick, completed ? recipe.RawXpPerWorkTick * workTicks : 0, SkillXpPath.NormalRecord);
    }

    /// <summary>One completed tend's callback: the native raw XP once, through the skill tracker, at the treatment's completion.</summary>
    public static SkillXpEvent TendCompletionLearning(TendLearning tend, string id, int tick)
        => new(id, tick, tend.RawXp, SkillXpPath.NormalTracker);
}
