using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>Mutable evaluation-local native skill arithmetic. No state leaves the forecast. The mechanics it reads are
/// known (<see cref="ForecastSkillPolicy.MechanicsGap"/> is null) before a cursor is made.</summary>
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
        mechanics = observed; level = observed.StoredLevel.Value; xp = observed.Xp.Value;
        midnight = observed.XpSinceMidnight.Value; nextDecay = observed.NextDecay.Value;
        nextReset = observed.NextMidnightReset.IsKnown ? observed.NextMidnightReset.Value : int.MaxValue;
        budget = observed.RangeAppliedBudget.IsKnown ? observed.RangeAppliedBudget.Value : 0;
        disabled = observed.Disabled.Value; allowed = observed.TrackerAllowsLearning.Value; aptitude = observed.Aptitude.Value;
        interval = observed.DecayInterval.Value; direct = observed.DirectFactor.Value; unsaturated = observed.UnsaturatedFactor.Value;
        threshold = observed.SaturationThreshold.Value; saturated = observed.SaturatedFactor.Value;
    }
    internal int StoredLevel => level;
    internal int Effective => disabled ? 0 : (int)Math.Clamp((long)level + aptitude, 0, mechanics.Levels.Count - 1);
    /// <summary>The tracker's next local midnight reset was read, so today's XP resets each day of a longer projection.</summary>
    internal bool ResetsDaily => nextReset != int.MaxValue;
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
        if ((callback.Practice || callback.Path == SkillXpPath.Range) && (!mechanics.PracticeEligible.Value || Effective >= mechanics.PracticeCeiling.Value)) return;
        Learn(callback.RawXp.Value, callback.Path);
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
    internal SkillLearning Rebase(int tick) => mechanics with
    {
        StoredLevel = Known(level), Xp = Known(xp), XpSinceMidnight = Known(midnight),
        NextDecay = Known(nextDecay - tick),
        TrackerElapsedTicks = Clock.ElapsedAt(tick),
        NextMidnightReset = nextReset == int.MaxValue ? default : Known(nextReset - tick),
        TrackerPhase = (mechanics.TrackerPhase - tick % mechanics.TrackerIntervalTicks + mechanics.TrackerIntervalTicks) % mechanics.TrackerIntervalTicks,
        ValidForTicks = Known(Math.Max(0, mechanics.ValidForTicks.Value - tick)),
        RangeAppliedBudget = mechanics.RangeAppliedBudget.IsKnown ? Known(budget) : default
    };
    private void Apply(double amount, bool isDirect)
    {
        xp += amount; if (!isDirect) midnight += amount;
        ForecastSkillPolicy.Settle(mechanics, ref level, ref xp);
    }
    internal SkillProjection Snapshot() => !double.IsFinite(xp) || !double.IsFinite(midnight) || !double.IsFinite(gain) || !double.IsFinite(loss)
        ? new(default, default, default, default, default, ["learning_arithmetic_overflow"])
        : new(Known(Effective), Known(level), Known(xp), Known(gain), Known(loss), []);
}
