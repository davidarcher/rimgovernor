using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>Mutable evaluation-local native skill arithmetic. No state leaves the forecast.</summary>
internal sealed class ForecastSkillCursor
{
    private readonly SkillLearning mechanics;
    private int level, nextDecay, nextReset;
    private double xp, midnight, gain, loss, budget;
    internal ForecastSkillCursor(SkillLearning observed)
    {
        mechanics = observed; level = observed.StoredLevel.Value; xp = observed.Xp.Value;
        midnight = observed.XpSinceMidnight.Value; nextDecay = observed.NextDecay.Value;
        nextReset = observed.NextMidnightReset.IsKnown ? observed.NextMidnightReset.Value : int.MaxValue;
        budget = observed.RangeAppliedBudget.IsKnown ? observed.RangeAppliedBudget.Value : 0;
    }
    internal int StoredLevel => level;
    internal int Effective => mechanics.Disabled.Value ? 0 : (int)Math.Clamp((long)level + mechanics.Aptitude.Value, 0, mechanics.Levels.Count - 1);
    private int DecayTick => nextDecay + (mechanics.TrackerPhase - nextDecay % mechanics.TrackerIntervalTicks + mechanics.TrackerIntervalTicks) % mechanics.TrackerIntervalTicks;
    internal void Advance(int tick)
    {
        while (Math.Min(DecayTick, nextReset) <= tick)
        {
            // SkillsTickInterval resets today's XP before calling each SkillRecord.Interval.
            if (nextReset <= DecayTick) { midnight = 0; nextReset += GameTime.TicksPerDay; }
            else
            {
                if (!mechanics.Disabled.Value && mechanics.TrackerAllowsLearning.Value)
                { var amount = mechanics.Levels[level].DecayXp; loss += amount; Apply(-amount, false); }
                nextDecay += mechanics.DecayInterval.Value;
            }
        }
    }
    internal void Learn(SkillXpEvent callback)
    {
        if (mechanics.Disabled.Value || callback.Path == SkillXpPath.NormalTracker && !mechanics.TrackerAllowsLearning.Value) return;
        if ((callback.Practice || callback.Path == SkillXpPath.Range) && (!mechanics.PracticeEligible.Value || Effective >= mechanics.PracticeCeiling.Value)) return;
        var direct = callback.Path == SkillXpPath.Range;
        var amount = callback.RawXp.Value * (direct ? mechanics.DirectFactor.Value : mechanics.UnsaturatedFactor.Value
            * (midnight > mechanics.SaturationThreshold.Value ? mechanics.SaturatedFactor.Value : 1));
        if (direct) { amount = Math.Min(amount, budget); budget -= amount; }
        gain += amount; Apply(amount, direct);
    }
    private void Apply(double amount, bool direct)
    {
        xp += amount; if (!direct) midnight += amount;
        ForecastSkillPolicy.Settle(mechanics, ref level, ref xp);
    }
    internal SkillProjection Snapshot() => !double.IsFinite(xp) || !double.IsFinite(midnight) || !double.IsFinite(gain) || !double.IsFinite(loss)
        ? new(default, default, default, default, default, ["learning_arithmetic_overflow"])
        : new(Known(Effective), Known(level), Known(xp), Known(gain), Known(loss), []);
}

