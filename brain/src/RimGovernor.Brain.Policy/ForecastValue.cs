using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The named, documented constants of the capability-days currency (#3469). Nothing else in the ledger is chosen by hand;
/// each default is an accepted starting value tuned by playtest (see kept-constants, "Policy constants"). The ledger
/// prices with <see cref="WellbeingWeight"/> today; the others travel with it so every decision reports the constants it
/// used, and are read by the floors and the harm term as they land.
/// </summary>
public sealed record ForecastValuePolicy
{
    /// <summary>Capability-days a colonist is worth beyond the horizon.</summary>
    public double DeathTerminalValue { get; init; } = 60;

    /// <summary>Capability-days per mood-day, for wellbeing valued for itself above what break risk counts.</summary>
    public double WellbeingWeight { get; init; } = 0.1;

    /// <summary>The CVaR tail of the raid-harm transform: the mean of the worst (1 - alpha) of outcomes.</summary>
    public double HarmRiskAlpha { get; init; } = 0.9;

    /// <summary>The weight of that tail against the expected loss in the raid-harm transform.</summary>
    public double HarmRiskWeight { get; init; } = 0.5;

    /// <summary>The floor: expected deaths per day above the baseline a portfolio may add.</summary>
    public double DeathRiskFloor { get; init; } = 0.01;

    /// <summary>The floor: expected mental breaks per pawn per day a portfolio may reach.</summary>
    public double BreakRiskCap { get; init; } = 0.5;

    /// <summary>Capability-days under which two values tie and the incumbent is kept.</summary>
    public double TieResolution { get; init; } = 0.01;

    /// <summary>Capability-days a whole goal is worth at each importance (#3503); a goal's progress inside the horizon earns that fraction of it.</summary>
    public GoalImportanceValues GoalImportanceValue { get; init; } = new(0.5, 2, 8);

    /// <summary>The multiplier on a goal's worth per day past its deadline (#3503); 1 until the deadline.</summary>
    public double GoalDeadlineDecay { get; init; } = 0.8;

    /// <summary>The least a goal's worth decays to (#3503), as a fraction of its full worth.</summary>
    public double GoalDeadlineFloor { get; init; } = 0.1;

    /// <summary>
    /// A goal's worth at <paramref name="tick"/>: its importance's value, full until <paramref name="deadlineDay"/> (days
    /// passed since the game began) and then <see cref="GoalDeadlineDecay"/> per day past it, never under <see cref="GoalDeadlineFloor"/>.
    /// </summary>
    public double GoalWorth(Importance importance, int? deadlineDay, long tick)
    {
        var late = deadlineDay is { } due ? tick / GameTime.TicksPerDay - due : 0;
        var decay = late <= 0 ? 1 : Math.Max(GoalDeadlineFloor, Math.Pow(GoalDeadlineDecay, late));
        return GoalImportanceValue.Of(importance) * decay;
    }

    /// <summary>The risk profile's row: the CVaR tail, its weight on raid harm and the death floor (#3503). Cautious is stricter than Normal, Bold looser.</summary>
    public sealed record RiskRow(double HarmRiskAlpha, double HarmRiskWeight, double DeathRiskFloor);

    public static readonly IReadOnlyDictionary<RiskProfile, RiskRow> RiskRows = new Dictionary<RiskProfile, RiskRow>
    {
        [RiskProfile.Cautious] = new(0.95, 0.8, 0.005),
        [RiskProfile.Normal] = new(0.9, 0.5, 0.01),
        [RiskProfile.Bold] = new(0.8, 0.2, 0.02),
    };

    /// <summary>The pace's row: capability-days per mood-day of wellbeing valued for itself (#3503).</summary>
    public static readonly IReadOnlyDictionary<Pace, double> PaceWellbeingWeight = new Dictionary<Pace, double>
    {
        [Pace.Wellbeing] = 0.2,
        [Pace.Balanced] = 0.1,
        [Pace.Development] = 0.05,
    };

    /// <summary>The constants under the player's risk profile and pace (#3503); the defaults for the default preferences. Floors are only moved, never relaxed by a goal.</summary>
    public static ForecastValuePolicy For(PreferencesRecord preferences)
    {
        var risk = RiskRows[preferences.RiskProfile];
        return new()
        {
            HarmRiskAlpha = risk.HarmRiskAlpha, HarmRiskWeight = risk.HarmRiskWeight, DeathRiskFloor = risk.DeathRiskFloor,
            WellbeingWeight = PaceWellbeingWeight[preferences.Pace],
        };
    }
}

/// <summary>
/// One outcome's capability-days lost over the horizon, term by term (#3520). Every term is a loss in capability-days
/// (one pawn-day at full capability is one), so a lower total is better; the wellbeing term is negative when the outcome
/// has more mood than the baseline. A term is unknown when its inputs are, and then so is the total; the findings name why.
/// </summary>
public sealed record ForecastValueResult(Fact<double> Total, EquatableArray<string> Findings)
{
    /// <summary>Capacity lost to malnutrition while a pawn starves, and for as long as the severity it accrued lasts.</summary>
    public Fact<double> Hunger { get; init; }

    /// <summary>Capacity lost to exhaustion: the game gives no rest category a capacity effect, so it is 0 once the rest timelines are known.</summary>
    public Fact<double> Rest { get; init; }

    /// <summary>Expected food-poisoning cases times the capacity one case loses over its course, clipped to the horizon.</summary>
    public Fact<double> FoodPoisoning { get; init; }

    /// <summary>Capacity lost to hypothermia and heatstroke over the severity the stationary thermal spans give them, and the remaining horizon plus the terminal value when it reaches lethal.</summary>
    public Fact<double> Thermal { get; init; }

    /// <summary>Mental-break risk times the mean days a break of its tier takes the colonist out of work.</summary>
    public Fact<double> MoodBreaks { get; init; }

    /// <summary>The wellbeing weight times the mood-days lost against the baseline outcome.</summary>
    public Fact<double> Wellbeing { get; init; }

    /// <summary>Raid harm and death: unassessed until the harm model (#3471), contributing 0.</summary>
    public Fact<double> Harm { get; init; }

    /// <summary>Capability added or loss avoided after completion: 0 until tier 2 (#3470).</summary>
    public Fact<double> Investment { get; init; }

    /// <summary>The player's goals the outcome progresses inside the horizon, in capability-days: negative (a gain) by the goals' worth times their progress (#3503). Known, 0 with no goal progressed.</summary>
    public Fact<double> Goals { get; init; } = Known(0d);

    /// <summary>Each goal behind <see cref="Goals"/>: its progress, worth and value.</summary>
    public EquatableArray<ForecastGoalContribution> GoalContributions { get; init; }

    /// <summary>
    /// This outcome's loss minus <paramref name="other"/>'s, summed over the terms BOTH assess (a negative number means this
    /// outcome loses less). A term either side cannot assess is left out of both, so missing data never rewards a side, and
    /// the comparison is unknown only when no term is shared (#3522). Compare totals directly only when every term is known.
    /// </summary>
    public Fact<double> LossAgainst(ForecastValueResult other)
    {
        double delta = 0;
        var shared = false;
        foreach (var (mine, theirs) in new[] { (Hunger, other.Hunger), (Rest, other.Rest), (FoodPoisoning, other.FoodPoisoning),
            (Thermal, other.Thermal), (MoodBreaks, other.MoodBreaks), (Wellbeing, other.Wellbeing), (Harm, other.Harm), (Investment, other.Investment), (Goals, other.Goals) })
        {
            if (!mine.TryGetValue(out var a) || !theirs.TryGetValue(out var b)) continue;
            delta += a - b;
            shared = true;
        }
        return shared ? Known(delta) : default;
    }
}

/// <summary>One safety floor of an outcome: whether it could be assessed, and by how much it is broken (0 when it holds).</summary>
public readonly record struct ForecastFloor(bool Assessed, double Excess)
{
    public bool Passed => Excess <= 0;
}

/// <summary>
/// Why an outcome ranks where it does (#3522): its capability-days ledger, the three safety floors and the constants used.
/// The ranking reads the floors, then the ledger, then the tiebreakers; this record is the explanation that is emitted.
/// </summary>
public sealed record ForecastValueExplanation(ForecastValueResult Ledger, ForecastValuePolicy Policy)
{
    /// <summary>Food or rest reaching zero: the excess is the longest zero-time in ticks. Unassessed when either timeline is unknown.</summary>
    public ForecastFloor UrgentZero { get; init; } = new(false, 0);

    /// <summary>A pawn's expected breaks per day over <see cref="ForecastValuePolicy.BreakRiskCap"/>: the excess is the worst pawn's overshoot.</summary>
    public ForecastFloor BreakRisk { get; init; } = new(false, 0);

    /// <summary>Unassessed until the harm model (#3471); always passes.</summary>
    public ForecastFloor Death { get; init; } = new(false, 0);

    /// <summary>The explanation of a vector assembled without a context: an empty ledger under the default constants.</summary>
    internal static readonly ForecastValueExplanation Empty = new(new(default, []), new());
}

/// <summary>The capability-days ledger: pure, term by term, every number from a game table or a named policy constant (#3520).</summary>
public static partial class ForecastValue
{
    // The five capacities a pawn-day of capability is counted in, by PawnCapacityDef name. Policy cannot reference the
    // game's PawnCapacityDefOf, so these are the names the catalog decodes the tables by (Catalog/CapacityPenalties.cs).
    private static readonly string[] Capacities = ["Consciousness", "Moving", "Manipulation", "Sight", "Talking"];

    // PawnCapacitiesHandler.CanBeAwake: a pawn below this consciousness is downed (Pawn_HealthTracker.ShouldBeDowned).
    internal const double AwakeConsciousness = 0.3;

    // Need_Food.BaseMalnutritionSeverityPerDay (0.0011325 per 150-tick interval): malnutrition gains this while starving
    // and loses the same amount otherwise (Need_Food.NeedInterval). The pawn's own factor, Lerp(0.8, 1.2, a seeded random),
    // is not observed, so the mean 1 is used.
    private const double MalnutritionSeverityPerDay = 0.453;

    /// <summary>One observed pawn as the ledger reads it: the row, and its five capacity levels (null when any is unobserved).</summary>
    private sealed record ObservedPawn(NeedsForecastPawn Pawn, double[]? Levels);

    /// <summary>A search prices thousands of outcomes against one context, so what the ledger reads from it is indexed once (#3468):
    /// its pawns by id with their capacity levels, and which penalty tables are valid.</summary>
    private sealed class LedgerIndex(ForecastCoupledContext context)
    {
        public readonly Dictionary<string, ObservedPawn> Pawns = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new ObservedPawn(g.First(), Levels(g.First())), StringComparer.Ordinal);
        public readonly bool Malnutrition = context.CapacityPenalties.Malnutrition.Valid();
        public readonly bool FoodPoisoning = context.CapacityPenalties.FoodPoisoning.Valid();
        public readonly bool Thermal = context.CapacityPenalties.Hypothermia.Valid() && context.CapacityPenalties.Heatstroke.Valid();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, LedgerIndex> Indexes = new();

    /// <summary>
    /// The ledger of <paramref name="outcome"/> over <paramref name="horizon"/> ticks. The wellbeing term is relative to
    /// <paramref name="baseline"/>, the outcome the comparison started from (so only differences matter), and is unknown without one.
    /// </summary>
    public static ForecastValueResult Of(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ForecastCoupledOutcome? baseline = null, IEnumerable<ForecastCommitment>? commitments = null)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var pawns = outcome.Needs.ToArray();
        Array.Sort(pawns, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        var index = Indexes.GetValue(context, static c => new LedgerIndex(c));
        var observed = index.Pawns;
        var tables = context.CapacityPenalties;

        var hunger = default(Fact<double>);
        if (!index.Malnutrition || tables.MovingMinForCapable is not { } movingMin)
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.Malnutrition));
        else if (pawns.Length == 0) findings.Add("hunger_unassessed:no_pawns");
        else
        {
            double sum = 0;
            var known = true;
            foreach (var (pawn, result) in pawns)
            {
                if (HungerLoss(pawn, result, observed.GetValueOrDefault(pawn), tables.Malnutrition, movingMin, horizon, findings) is { } lost) sum += lost;
                else known = false;
            }
            hunger = known ? Known(sum) : default;
        }

        var rest = default(Fact<double>);
        if (pawns.Length == 0) findings.Add("rest_unassessed:no_pawns");
        else
        {
            var known = true;
            foreach (var (pawn, result) in pawns)
            {
                // No rest category carries a capacity effect in the game, so a known rest timeline costs nothing; an unknown one is unassessed.
                if (Need(result, "rest") is { } need && !need.ZeroTicks.IsKnown)
                {
                    findings.Add("rest_unassessed:" + pawn);
                    known = false;
                }
            }
            rest = known ? Known(0d) : default;
        }

        var poisoning = default(Fact<double>);
        if (!index.FoodPoisoning || tables.MovingMinForCapable is not { } poisonMoving)
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.FoodPoisoning));
        else if (!outcome.Support.FoodPoisonExposure.TryGetValue(out var cases))
            findings.Add("food_poison_exposure_unassessed");
        else
            poisoning = FoodPoisoningLoss(cases, pawns, observed, tables.FoodPoisoning, poisonMoving, horizon, findings);

        var thermal = default(Fact<double>);
        if (!index.Thermal || tables.MovingMinForCapable is not { } thermalMoving)
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.Hypothermia) + "/" + nameof(tables.Heatstroke));
        else if (pawns.Length == 0) findings.Add("thermal_unassessed:no_pawns");
        else
        {
            double sum = 0;
            var known = true;
            foreach (var (pawn, _) in pawns)
            {
                ForecastThermalExposure? exposure = null;
                foreach (var t in outcome.ThermalExposure)
                    if (t.Pawn == pawn) { exposure = t; break; }
                if (ThermalLoss(pawn, exposure, observed.GetValueOrDefault(pawn), tables, thermalMoving, horizon, context.Value, findings) is { } lost) sum += lost;
                else known = false;
            }
            thermal = known ? Known(sum) : default;
        }

        var breaks = default(Fact<double>);
        if (pawns.Length == 0) findings.Add("mood_breaks_unassessed:no_pawns");
        else
        {
            double sum = 0;
            var known = true;
            foreach (var (_, result) in pawns)
            {
                if (result.Mood is { BreakRisk.IsKnown: true } mood && context.MoodBreaks.DaysLost(mood.BreakTierTicks) is { } days) sum += days;
                else { findings.Add("mental_break_probability_unassessed"); known = false; }
            }
            breaks = known ? Known(sum) : default;
        }

        var wellbeing = default(Fact<double>);
        if (baseline is null) findings.Add("wellbeing_baseline_missing");
        else if (pawns.Length == 0) findings.Add("wellbeing_unassessed:no_pawns");
        else
        {
            double sum = 0;
            var known = true;
            foreach (var (pawn, result) in pawns)
            {
                if (result.Mood?.PressureTicks.TryGetValue(out var pressure) == true && baseline.Needs.GetValueOrDefault(pawn)?.Mood?.PressureTicks.TryGetValue(out var before) == true)
                    // Mood-days are (horizon - pressure) / a day, so the loss against the baseline is the extra pressure.
                    sum += context.Value.WellbeingWeight * (pressure - before) / GameTime.TicksPerDay;
                else { findings.Add("wellbeing_unassessed:" + pawn); known = false; }
            }
            wellbeing = known ? Known(sum) : default;
        }

        findings.Add("harm_capability_unassessed");
        var contributions = GoalContributionsOf(outcome, context, commitments ?? []);
        double earned = 0;
        foreach (var contribution in contributions) earned += contribution.Value;
        var goals = Known(-earned);
        var total = hunger.IsKnown && rest.IsKnown && poisoning.IsKnown && thermal.IsKnown && breaks.IsKnown && wellbeing.IsKnown
            ? Known(hunger.Value + rest.Value + poisoning.Value + thermal.Value + breaks.Value + wellbeing.Value + goals.Value) : default;
        if (!total.IsKnown) findings.Add("capability_value_unassessed");
        return new(total, EquatableArray.From(findings))
        {
            Hunger = hunger, Rest = rest, FoodPoisoning = poisoning, Thermal = thermal, MoodBreaks = breaks, Wellbeing = wellbeing,
            Harm = Known(0d), Investment = Known(0d), Goals = goals, GoalContributions = EquatableArray.From(contributions),
        };
    }

    /// <summary>
    /// What the outcome earns of the player's goals (#3503). Research: the points its scheduled work at a goal's project adds,
    /// at the goal's share per point. Relief: each requirement relief a selected commitment carries that a goal injected, once
    /// per relief need, at the goal's share. A goal's progress is capped at the whole goal; its value is the progress times
    /// the goal's worth at the observed tick, which carries the deadline's decay.
    /// </summary>
    private static List<ForecastGoalContribution> GoalContributionsOf(ForecastCoupledOutcome outcome, ForecastCoupledContext context,
        IEnumerable<ForecastCommitment> commitments)
    {
        var result = new List<ForecastGoalContribution>();
        if (context.GoalValues.Count == 0) return result;
        var progress = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var allocation in outcome.Plan.Allocations)
            if (context.Activities.Activities.FirstOrDefault(a => a.Id == allocation.Activity)?.ProjectProgress is { } research)
                progress[research.Project] = progress.GetValueOrDefault(research.Project) + allocation.WorkTicks * research.PointsPerTick;
        var relief = commitments.Where(c => c.Outcome?.Requirements is not null).SelectMany(c => c.Outcome!.Requirements!.Relief)
            .GroupBy(r => r.Need, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Math.Min(1, g.Sum(r => r.Fraction)), StringComparer.Ordinal);
        foreach (var goal in context.GoalValues)
        {
            var share = goal.Research.Sum(r => progress.GetValueOrDefault(r.Project) * r.SharePerPoint)
                + goal.Relief.Sum(r => relief.GetValueOrDefault(r.Need) * r.Share);
            share = Math.Min(1, share);
            if (share <= 0) continue;
            var worth = context.Value.GoalWorth(goal.Importance, goal.DeadlineDay, context.Needs.Tick);
            result.Add(new(goal.Goal, share, worth, share * worth));
        }
        return result;
    }

    /// <summary>The ledger and the floors of one outcome, as the comparison reads them (#3522). Wellbeing is against <paramref name="reference"/>, the same for every outcome of a search.</summary>
    public static ForecastValueExplanation Explain(ForecastCoupledOutcome outcome, Fact<double> urgentZeroTicks, ForecastCoupledContext context,
        double horizon, ForecastCoupledOutcome reference, IEnumerable<ForecastCommitment>? commitments = null)
    {
        var policy = context.Value;
        var days = horizon / GameTime.TicksPerDay;
        var worst = 0d;
        var assessed = days > 0 && outcome.Needs.Count > 0;
        foreach (var result in outcome.Needs.Values)
        {
            if (result.Mood is { BreakRisk: { IsKnown: true } risk } && days > 0) worst = Math.Max(worst, risk.Value / days - policy.BreakRiskCap);
            else assessed = false;
        }
        return new(Of(outcome, context, horizon, reference, commitments), policy)
        {
            UrgentZero = new(urgentZeroTicks.IsKnown, urgentZeroTicks.IsKnown ? Math.Max(0, urgentZeroTicks.Value) : 0),
            BreakRisk = new(assessed, worst),
            Death = new(false, 0),
        };
    }

    private static ForecastNeedMetric? Need(ForecastNeedsResult result, string kind)
    {
        foreach (var need in result.Needs)
            if (need.Kind == kind) return need;
        return null;
    }

    /// <summary>
    /// A pawn's capability-days lost to malnutrition: its severity from the observed one, rising at the game's rate on the
    /// spans its food is zero and falling at the same rate otherwise, each stretch priced at its stage's capability factor.
    /// Null when the pawn's food timeline or capacities are unobserved.
    /// </summary>
    private static double? HungerLoss(string pawn, ForecastNeedsResult result, ObservedPawn? seen, HediffPenaltyTable table,
        double movingMin, double horizon, SortedSet<string> findings)
    {
        var food = Need(result, "food");
        if (food is null) return 0;
        if (!food.ZeroTicks.IsKnown) { findings.Add("hunger_unassessed:" + pawn); return null; }
        if (seen is null || !seen.Pawn.MalnutritionSeverity.TryGetValue(out var severity) || seen.Levels is not { } levels)
        { findings.Add("capacities_unobserved:" + pawn); return null; }
        var course = new Course(table, levels, movingMin, severity);
        double cursor = 0;
        var spans = food.ZeroSpans;
        Span<int> order = spans.Count <= 64 ? stackalloc int[spans.Count] : new int[spans.Count];
        ByStart(spans, order);
        foreach (var i in order)
        {
            var span = spans[i];
            course.Advance(span.Start - cursor, -MalnutritionSeverityPerDay);
            course.Advance(span.End - Math.Max(span.Start, cursor), MalnutritionSeverityPerDay);
            cursor = Math.Max(cursor, span.End);
        }
        course.Advance(horizon - cursor, -MalnutritionSeverityPerDay);
        course.Report(pawn, findings);
        if (spans.Count > 0) findings.Add("malnutrition_rate_pawn_factor_unobserved:" + pawn);
        return course.LostTicks / GameTime.TicksPerDay;
    }

    /// <summary>The spans' indexes by Start, ties in span order (a stable sort, as OrderBy ranks them), with nothing allocated (#3468).</summary>
    private static void ByStart(EquatableArray<ForecastNeedSpan> spans, Span<int> order)
    {
        for (var i = 0; i < order.Length; i++) order[i] = i;
        order.Sort(new NeedSpanOrder(spans));
    }

    private readonly struct NeedSpanOrder(EquatableArray<ForecastNeedSpan> spans) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var c = spans[x].Start.CompareTo(spans[y].Start);
            return c != 0 ? c : x.CompareTo(y);
        }
    }

    /// <summary>
    /// The expected cases times the capability-days one case loses: its severity starts at the def's initial severity and
    /// follows the SeverityPerDay comp until it is gone, the Disappears comp's mean ends it, or the horizon does. The case
    /// lands on one of the colony's pawns, unknown which, so it is the mean over them; it is counted from the horizon's
    /// start, as the meal that causes it is not placed.
    /// </summary>
    private static Fact<double> FoodPoisoningLoss(double cases, KeyValuePair<string, ForecastNeedsResult>[] pawns,
        Dictionary<string, ObservedPawn> observed, HediffPenaltyTable table, double movingMin, double horizon, SortedSet<string> findings)
    {
        if (pawns.Length == 0) { findings.Add("food_poisoning_unassessed:no_pawns"); return default; }
        var length = table.DisappearsAfterTicks is { } gone ? Math.Min(horizon, (gone.Min + gone.Max) / 2) : horizon;
        double sum = 0;
        foreach (var (pawn, _) in pawns)
        {
            if (!observed.TryGetValue(pawn, out var seen) || seen.Levels is not { } levels)
            { findings.Add("capacities_unobserved:" + pawn); return default; }
            // The pawn's own current poisoning, if any, is the stage its capacities already carry.
            var course = new Course(table, levels, movingMin, seen.Pawn.FoodPoisoningSeverity.ValueOrDefault) { Severity = table.InitialSeverity };
            course.Advance(length, table.SeverityPerDay ?? 0);
            course.Report(pawn, findings);
            sum += course.LostTicks / GameTime.TicksPerDay;
        }
        return Known(cases * sum / pawns.Length);
    }

    /// <summary>The pawn's five observed capacity levels, in <see cref="Capacities"/> order; null when any is unobserved.</summary>
    internal static double[]? Levels(NeedsForecastPawn pawn) =>
        pawn.Consciousness.TryGetValue(out var consciousness) && pawn.Moving.TryGetValue(out var moving)
            && pawn.Manipulation.TryGetValue(out var manipulation) && pawn.Sight.TryGetValue(out var sight)
            && pawn.Talking.TryGetValue(out var talking)
            ? [consciousness, moving, manipulation, sight, talking]
            : null;

    /// <summary>
    /// One hediff's severity walked over time against the pawn's observed capacities. A stretch in a stage costs
    /// (1 - the capability factor) of its duration, where the factor is the product over the five capacities of the level
    /// the stage leaves (the game's (base + offsets) * post factors, held under the smallest ceiling; PawnCapacityUtility.CalculateCapacityLevel)
    /// against the observed one, and 0 for a pawn the stage downs that the observed levels do not.
    /// A struct walked in place (#3468): a search prices thousands of outcomes, each with several courses a pawn.
    /// </summary>
    internal struct Course(HediffPenaltyTable table, double[] observed, double movingMin, double observedSeverity)
    {
        private readonly PenaltyStage? current = table.StageAt(observedSeverity);
        private bool pain;
        private bool lethal;

        public void MarkLethal() => lethal = true;

        /// <summary>Whether the severity reached the def's lethal value.</summary>
        public readonly bool Lethal => lethal;

        /// <summary>Whether some stretch of the course left the pawn downed (a stage that downs a pawn the observed levels do not).</summary>
        public bool Downs { get; private set; }

        public readonly HediffPenaltyTable Table => table;

        public double Severity { get; set; } = observedSeverity;

        public double LostTicks { get; private set; }

        /// <summary>Moves the severity by <paramref name="perDay"/> for <paramref name="ticks"/>, cut at the stage boundaries, at 0 (the hediff is gone) and at the lethal severity (held there).</summary>
        public void Advance(double ticks, double perDay)
        {
            var rate = perDay / GameTime.TicksPerDay;
            while (ticks > 0)
            {
                if (rate < 0 && Severity <= 0) { Severity = 0; return; }
                if (rate == 0 || table.LethalSeverity is { } top && rate > 0 && Severity >= top)
                {
                    Piece(ticks, Severity, Severity);
                    lethal |= rate != 0;
                    return;
                }

                var stop = Severity + rate * ticks;
                var cut = false;
                // The boundaries: each stage's minimum, 0, then the lethal severity, in that order.
                var stages = table.Stages;
                var count = stages.Count + (table.LethalSeverity is null ? 1 : 2);
                for (var i = 0; i < count; i++)
                {
                    var boundary = i < stages.Count ? stages[i].MinSeverity : i == stages.Count ? 0 : table.LethalSeverity!.Value;
                    if (rate > 0 ? boundary > Severity && boundary < stop : boundary < Severity && boundary > stop)
                    {
                        stop = boundary;
                        cut = true;
                    }
                }

                var piece = cut ? (stop - Severity) / rate : ticks;
                Piece(piece, Severity, stop);
                Severity = stop;
                ticks -= piece;
                if (!cut) return;
            }
        }

        public readonly void Report(string pawn, SortedSet<string> findings)
        {
            if (pain) findings.Add("hediff_pain_unpriced:" + table.Hediff);
            if (lethal) findings.Add("lethal_severity_reached:" + table.Hediff + ":" + pawn);
        }

        public void Piece(double ticks, double from, double to)
        {
            if (table.StageAt((from + to) / 2) is not { } stage) return;
            pain |= stage.PainOffset > 0;
            var factor = Factor(stage);
            Downs |= factor <= 0 && ticks > 0;
            LostTicks += (1 - factor) * ticks;
        }

        private readonly double Factor(PenaltyStage stage)
        {
            Span<double> after = stackalloc double[Capacities.Length];
            for (var i = 0; i < Capacities.Length; i++)
            {
                var (offset, post, max) = Modifiers(current, Capacities[i]);
                var (nextOffset, nextPost, nextMax) = Modifiers(stage, Capacities[i]);
                // The level the pawn would have without this hediff: undo the current stage; a pawn held at the current
                // ceiling has lost what a stage cannot undo, so it is taken as whole.
                var level = observed[i];
                var baseLevel = current is null ? level : level >= max ? 1 : post > 0 ? level / post - offset : level;
                after[i] = Math.Max(0, Math.Min((baseLevel + nextOffset) * nextPost, nextMax));
            }

            var downedNow = Downed(observed);
            if (downedNow) return 1;
            if (Downed(after)) return 0;
            var factor = 1d;
            for (var i = 0; i < Capacities.Length; i++)
                if (observed[i] > 0) factor *= Math.Clamp(after[i] / observed[i], 0, 1);
            return factor;
        }

        private readonly bool Downed(ReadOnlySpan<double> levels) => levels[0] < AwakeConsciousness || levels[1] <= movingMin;

        /// <summary>A stage's effect on one capacity as the game applies it: the offsets summed, the post factors multiplied, the smallest ceiling.</summary>
        private static (double Offset, double Post, double Max) Modifiers(PenaltyStage? stage, string capacity)
        {
            double offset = 0, post = 1, max = double.PositiveInfinity;
            if (stage is null) return (offset, post, max);
            var modifiers = stage.Modifiers;
            for (var k = 0; k < modifiers.Count; k++)
            {
                var m = modifiers[k];
                if (m.Capacity != capacity) continue;
                offset += m.Offset;
                post *= m.PostFactor;
                max = Math.Min(max, m.SetMax ?? double.PositiveInfinity);
            }
            return (offset, post, max);
        }
    }
}
