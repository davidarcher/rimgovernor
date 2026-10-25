using System.Collections.Concurrent;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a unit of a skill's XP is worth to an outcome (#3912): the stock's marginal value tier 2 priced (<c>Investment[skill/pawn/skill]</c>,
/// the harm a level saves) and the work the level speeds up. The work part is the pawn's work added at the work speeds one more stored
/// level gives it (<see cref="PawnUpkeepRead.SkillSpeeds"/>) less its work added now (<see cref="PawnValueRules.WorkAdded"/>, against the
/// outcome's shortfall, <see cref="JoinerMoodRules.ShortfallOf"/>), over the days that level lasts inside the investment horizon
/// (<see cref="ProjectMarginals.SkillRaises"/>: decay takes it back), over the native XP the level takes. A skill no needed work reads gains
/// nothing by its level, so its work part is 0; a skill at the top level has no level to gain. A stock tier 2 gave no marginal is
/// unvalued (null). A fact the part needs and the review lacks (the pawn's upkeep, its free hours, its stock) throws
/// <see cref="RequiredFactException"/>.
/// </summary>
internal static class SkillValue
{
    private sealed class Table(WorkShortfall shortfall, IReadOnlyDictionary<string, double> hours)
    {
        public WorkShortfall Shortfall { get; } = shortfall;

        public IReadOnlyDictionary<string, double> Hours { get; } = hours;

        public ConcurrentDictionary<string, double> WorkPerXp { get; } = new(StringComparer.Ordinal);
    }

    // The outcome's shortfall and the colony's free hours, computed once per outcome beside the prices of its skills.
    private static readonly PerOutcome<Table> Tables = new((outcome, commitments, context, horizon) =>
    {
        return new Table(JoinerMoodRules.ShortfallOf(outcome, commitments, context, horizon), PawnValueRules.FreeHoursOf(outcome, context));
    });

    /// <summary>The capability-days a unit of XP of <paramref name="skill"/> of <paramref name="pawn"/> adds, or null when tier 2 priced no marginal for the stock.</summary>
    internal static double? PerXp(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon, string pawn, string skill)
    {
        var key = ProjectGraph.SkillKey(pawn, skill);
        if (!context.Investment.TryGetValue(key, out var priced)) return null;
        var raise = context.SkillRaises.TryGetValue(key, out var found) ? found : throw new RequiredFactException("skill raise of " + key);
        var upkeep = (context.Needs.Pawns.FirstOrDefault(p => p.Pawn == pawn) ?? throw new RequiredFactException("upkeep of " + pawn)).Upkeep;
        // A stock at the top level, or a skill no work speed of the pawn reads, speeds nothing up: no shortfall or free hours are needed.
        if (raise.Xp <= 0 || !upkeep.SkillSpeeds.Any(r => r.Skill == skill)
            && upkeep.ChildWork?.Ages.Any(a => a.SkillSpeeds.Any(r => r.Skill == skill)) != true) return priced;
        var table = Tables.Of(outcome, commitments, context, horizon);
        return priced + table.WorkPerXp.GetOrAdd(key, _ => WorkPerXp(table, context, pawn, skill, upkeep, raise));
    }

    private static double WorkPerXp(Table table, ForecastCoupledContext context, string pawn, string skill, PawnUpkeepRead upkeep, SkillRaise raise)
    {
        var free = (table.Hours.TryGetValue(pawn, out var hours) ? hours : throw new RequiredFactException("free hours of " + pawn)) * GameTime.TicksPerHour;
        return WorkPerXp(pawn, skill, upkeep, raise, free, table.Shortfall, context.InvestmentHorizonDays);
    }

    /// <summary>
    /// The work part of a unit of XP from plain values: the work added at the speeds one more level gives (<paramref name="upkeep"/>'s
    /// <see cref="PawnUpkeepRead.SkillSpeeds"/> of <paramref name="skill"/>) less the work added now, in capability-days a day, over the
    /// <paramref name="raise"/> days it lasts inside <paramref name="horizonDays"/>, over the XP the level takes. 0 for a skill no
    /// speed reads and for a stock at the top level (<see cref="SkillRaise.Xp"/> 0).
    /// </summary>
    internal static double WorkPerXp(string pawn, string skill, PawnUpkeepRead upkeep, SkillRaise raise, double freeTicksPerDay, WorkShortfall shortfall, double horizonDays)
    {
        if (raise.Xp <= 0) return 0;
        var lasts = Math.Min(horizonDays, raise.Days);
        if (lasts <= 0) return 0;
        if (upkeep.ChildWork is { } child)
        {
            if (child.Ages.Count == 0 || child.Ages[0].BiologicalYears != (int)child.BiologicalYears)
                throw new RequiredFactException("child work ages of " + pawn);
            var value = 0d;
            for (var index = 0; index < child.Ages.Count; index++)
            {
                var age = child.Ages[index];
                var from = ChildWorkRules.DayAt(child, age.BiologicalYears);
                if (from >= lasts) break;
                var to = index + 1 < child.Ages.Count ? Math.Min(lasts, ChildWorkRules.DayAt(child, child.Ages[index + 1].BiologicalYears)) : lasts;
                value += At(age.Speeds, age.SkillSpeeds, age.IncapableWork) * (to - from);
            }
            return value / GameTime.TicksPerDay / raise.Xp;
        }
        return At(upkeep.Speeds, upkeep.SkillSpeeds, upkeep.IncapableWork) * lasts / GameTime.TicksPerDay / raise.Xp;

        double At(EquatableArray<WorkSpeedRead> work, EquatableArray<SkillSpeedRead> gains, EquatableArray<string> incapable)
        {
            var rows = gains.Where(r => r.Skill == skill).ToArray();
            if (rows.Length == 0) return 0;
            var speeds = work.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal);
            double Added(IReadOnlyDictionary<string, double> at) =>
                PawnValueRules.WorkAdded(new PawnValueFacts(pawn) { Speeds = at, Incapable = incapable, FreeTicksPerDay = freeTicksPerDay }, shortfall).Sum(s => s.TicksPerDay);
            var now = Added(speeds);
            var raised = new Dictionary<string, double>(speeds, StringComparer.Ordinal);
            foreach (var row in rows) raised[row.Stat] = row.OneLevelUp;
            return Added(raised) - now;
        }
    }
}
