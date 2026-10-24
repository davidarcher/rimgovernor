using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>Mutable evaluation-local native skill arithmetic. No state leaves the forecast. The mechanics it reads are
/// inside the supported window (<see cref="ForecastSkillPolicy.MechanicsGap"/> is null) before a cursor is made.</summary>
internal sealed class ForecastSkillCursor
{
    private readonly SkillLearning mechanics;
    private readonly bool disabled, allowed;
    private readonly int aptitude, interval;
    private readonly double direct, unsaturated, threshold, saturated;
    private int level, nextDecay, nextReset;
    private double xp, midnight, gain, loss, budget;
    internal ForecastSkillCursor(SkillLearning observed)
    {
        mechanics = observed; level = observed.StoredLevel; xp = observed.Xp;
        midnight = observed.XpSinceMidnight; nextDecay = observed.NextDecay;
        nextReset = observed.NextMidnightReset;
        budget = observed.Practice is SkillPractice.Range range ? range.AppliedBudget : 0;
        disabled = observed.Disabled; allowed = observed.TrackerAllowsLearning; aptitude = observed.Aptitude;
        interval = observed.DecayInterval; direct = observed.DirectFactor; unsaturated = observed.UnsaturatedFactor;
        threshold = observed.SaturationThreshold; saturated = observed.SaturatedFactor;
    }
    internal int StoredLevel => level;
    internal int Effective => disabled ? 0 : (int)Math.Clamp((long)level + aptitude, 0, mechanics.Levels.Count - 1);
    private TickIntervalClock Clock => new(mechanics.TrackerIntervalTicks, mechanics.TrackerPhase, mechanics.TrackerElapsedTicks);
    private int DecayTick => Clock.AtOrAfter(nextDecay);
    internal void Advance(int tick)
    {
        while (Math.Min(DecayTick, nextReset) <= tick)
        {
            // SkillsTickInterval resets today's XP before calling each SkillRecord.Interval.
            if (nextReset <= DecayTick) { if (allowed) midnight = 0; nextReset += GameTime.TicksPerDay; }
            else
            {
                if (!disabled && allowed)
                { var amount = mechanics.Levels[level].DecayXp; loss += amount; Apply(-amount, false); }
                nextDecay += interval;
            }
        }
    }
    internal void Learn(SkillXpEvent callback)
    {
        if (disabled || callback.Path == SkillXpPath.NormalTracker && !allowed) return;
        if ((callback.Practice || callback.Path == SkillXpPath.Range)
            && (mechanics.Practice is not SkillPractice.Drill { Eligible: true } drill || Effective >= drill.Ceiling)) return;
        Learn(callback.RawXp, callback.Path);
    }
    internal void Learn(double raw, SkillXpPath path)
    {
        if (disabled || path == SkillXpPath.NormalTracker && !allowed) return;
        var isDirect = path == SkillXpPath.Range;
        var amount = raw * (isDirect ? direct : unsaturated * (midnight > threshold ? saturated : 1));
        if (isDirect) { amount = Math.Min(amount, budget); budget -= amount; }
        gain += amount; Apply(amount, isDirect);
    }
    /// <summary>Ordinary tracker practice until today's XP reaches the full-rate threshold (SkillRecord.MaxFullRateXpPerDay),
    /// the cap #3813 prices sparring at: the raw XP that takes at the pawn's learn rate, applied at full rate. Nothing once
    /// today's XP is at the threshold, or at no learn rate.</summary>
    internal void LearnToSaturation()
    {
        if (unsaturated > 0 && midnight < threshold) Learn((threshold - midnight) / unsaturated, SkillXpPath.NormalTracker);
    }
    /// <summary>A day of the range drill (#3846): the drill's direct Learn applies XP up to the daily budget
    /// (RangeTraining.DailyXpBudget, <see cref="SkillPractice.Range.DailyBudget"/>), so the day's budget is refilled and the raw XP
    /// that takes at the direct learn rate applied. Nothing at no direct learn rate. Only Shooting has a range drill.</summary>
    internal void LearnRangeDay()
    {
        budget = mechanics.Practice is SkillPractice.Range range ? range.DailyBudget
            : throw new InvalidOperationException("a range day for a skill without the range drill");
        if (direct > 0) Learn(budget / direct, SkillXpPath.Range);
    }
    internal SkillLearning Rebase(int tick) => mechanics with
    {
        StoredLevel = level, Xp = xp, XpSinceMidnight = midnight,
        NextDecay = nextDecay - tick,
        TrackerElapsedTicks = Clock.ElapsedAt(tick),
        NextMidnightReset = nextReset - tick,
        TrackerPhase = (mechanics.TrackerPhase - tick % mechanics.TrackerIntervalTicks + mechanics.TrackerIntervalTicks) % mechanics.TrackerIntervalTicks,
        ValidForTicks = Math.Max(0, mechanics.ValidForTicks - tick),
        Practice = mechanics.Practice is SkillPractice.Range range ? range with { AppliedBudget = budget } : mechanics.Practice
    };
    private void Apply(double amount, bool isDirect)
    {
        xp += amount; if (!isDirect) midnight += amount;
        ForecastSkillPolicy.Settle(mechanics, ref level, ref xp);
    }
    /// <summary>The projection at <paramref name="tick"/>, the mechanics rebased to it; not projectable once the arithmetic overflows.</summary>
    internal SkillForecast Snapshot(int tick) => !double.IsFinite(xp) || !double.IsFinite(midnight) || !double.IsFinite(gain) || !double.IsFinite(loss)
        ? new SkillForecast.Unprojectable(["learning_arithmetic_overflow"])
        : new SkillForecast.Projected(new(Effective, level, xp, gain, loss, Rebase(tick)));
}
