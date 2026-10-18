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

    /// <summary>Mental-break risk times the mean days a break of its tier takes the colonist out of work.</summary>
    public Fact<double> MoodBreaks { get; init; }

    /// <summary>The wellbeing weight times the mood-days lost against the baseline outcome.</summary>
    public Fact<double> Wellbeing { get; init; }

    /// <summary>Raid harm and death: unassessed until the harm model (#3471), contributing 0.</summary>
    public Fact<double> Harm { get; init; }

    /// <summary>Capability added or loss avoided after completion: 0 until tier 2 (#3470).</summary>
    public Fact<double> Investment { get; init; }
}

/// <summary>The capability-days ledger: pure, term by term, every number from a game table or a named policy constant (#3520).</summary>
public static class ForecastValue
{
    // The five capacities a pawn-day of capability is counted in, by PawnCapacityDef name. Policy cannot reference the
    // game's PawnCapacityDefOf, so these are the names the catalog decodes the tables by (Catalog/CapacityPenalties.cs).
    private static readonly string[] Capacities = ["Consciousness", "Moving", "Manipulation", "Sight", "Talking"];

    // PawnCapacitiesHandler.CanBeAwake: a pawn below this consciousness is downed (Pawn_HealthTracker.ShouldBeDowned).
    private const double AwakeConsciousness = 0.3;

    // Need_Food.BaseMalnutritionSeverityPerDay (0.0011325 per 150-tick interval): malnutrition gains this while starving
    // and loses the same amount otherwise (Need_Food.NeedInterval). The pawn's own factor, Lerp(0.8, 1.2, a seeded random),
    // is not observed, so the mean 1 is used.
    private const double MalnutritionSeverityPerDay = 0.453;

    /// <summary>
    /// The ledger of <paramref name="outcome"/> over <paramref name="horizon"/> ticks. The wellbeing term is relative to
    /// <paramref name="baseline"/>, the outcome the comparison started from (so only differences matter), and is unknown without one.
    /// </summary>
    public static ForecastValueResult Of(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ForecastCoupledOutcome? baseline = null)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var pawns = outcome.Needs.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var observed = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // A term over the colony's pawns: each pawn's loss, or unknown when any pawn's is; a colony of none is unassessed.
        Fact<double> OverPawns(string term, Func<string, ForecastNeedsResult, NeedsForecastPawn?, double?> loss)
        {
            if (pawns.Length == 0) { findings.Add(term + "_unassessed:no_pawns"); return default; }
            double sum = 0;
            var known = true;
            foreach (var (pawn, result) in pawns)
            {
                if (loss(pawn, result, observed.GetValueOrDefault(pawn)) is { } lost) sum += lost;
                else known = false;
            }
            return known ? Known(sum) : default;
        }

        var tables = context.CapacityPenalties;
        var hunger = default(Fact<double>);
        if (!tables.Malnutrition.Valid() || tables.MovingMinForCapable is not { } movingMin)
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.Malnutrition));
        else
            hunger = OverPawns("hunger", (pawn, result, seen) => HungerLoss(pawn, result, seen, tables.Malnutrition, movingMin, horizon, findings));

        var rest = OverPawns("rest", (pawn, result, _) =>
        {
            // No rest category carries a capacity effect in the game, so a known rest timeline costs nothing; an unknown one is unassessed.
            if (result.Needs.FirstOrDefault(n => n.Kind == "rest") is { } need && !need.ZeroTicks.IsKnown)
            {
                findings.Add("rest_unassessed:" + pawn);
                return null;
            }
            return 0;
        });

        var poisoning = default(Fact<double>);
        if (!tables.FoodPoisoning.Valid() || tables.MovingMinForCapable is not { } poisonMoving)
            findings.Add("capacity_penalty_table_unknown:" + nameof(tables.FoodPoisoning));
        else if (!outcome.Support.FoodPoisonExposure.TryGetValue(out var cases))
            findings.Add("food_poison_exposure_unassessed");
        else
            poisoning = FoodPoisoningLoss(cases, pawns, observed, tables.FoodPoisoning, poisonMoving, horizon, findings);

        var breaks = OverPawns("mood_breaks", (pawn, result, _) =>
        {
            if (result.Mood is { BreakRisk.IsKnown: true } mood && context.MoodBreaks.DaysLost(mood.BreakTierTicks) is { } days) return days;
            findings.Add("mental_break_probability_unassessed");
            return null;
        });

        var wellbeing = default(Fact<double>);
        if (baseline is null) findings.Add("wellbeing_baseline_missing");
        else
            wellbeing = OverPawns("wellbeing", (pawn, result, _) =>
            {
                if (result.Mood?.PressureTicks.TryGetValue(out var pressure) == true && baseline.Needs.GetValueOrDefault(pawn)?.Mood?.PressureTicks.TryGetValue(out var before) == true)
                    // Mood-days are (horizon - pressure) / a day, so the loss against the baseline is the extra pressure.
                    return context.Value.WellbeingWeight * (pressure - before) / GameTime.TicksPerDay;
                findings.Add("wellbeing_unassessed:" + pawn);
                return null;
            });

        findings.Add("harm_capability_unassessed");
        var terms = new[] { hunger, rest, poisoning, breaks, wellbeing };
        var total = terms.All(t => t.IsKnown) ? Known(terms.Sum(t => t.Value)) : default;
        if (!total.IsKnown) findings.Add("capability_value_unassessed");
        return new(total, EquatableArray.From(findings))
        {
            Hunger = hunger, Rest = rest, FoodPoisoning = poisoning, MoodBreaks = breaks, Wellbeing = wellbeing,
            Harm = Known(0d), Investment = Known(0d),
        };
    }

    /// <summary>
    /// A pawn's capability-days lost to malnutrition: its severity from the observed one, rising at the game's rate on the
    /// spans its food is zero and falling at the same rate otherwise, each stretch priced at its stage's capability factor.
    /// Null when the pawn's food timeline or capacities are unobserved.
    /// </summary>
    private static double? HungerLoss(string pawn, ForecastNeedsResult result, NeedsForecastPawn? seen, HediffPenaltyTable table,
        double movingMin, double horizon, SortedSet<string> findings)
    {
        var food = result.Needs.FirstOrDefault(n => n.Kind == "food");
        if (food is null) return 0;
        if (!food.ZeroTicks.IsKnown) { findings.Add("hunger_unassessed:" + pawn); return null; }
        if (seen is null || !seen.MalnutritionSeverity.TryGetValue(out var severity) || Levels(seen) is not { } levels)
        { findings.Add("capacities_unobserved:" + pawn); return null; }
        var course = new Course(table, levels, movingMin, severity);
        double cursor = 0;
        foreach (var span in food.ZeroSpans.OrderBy(s => s.Start))
        {
            course.Advance(span.Start - cursor, -MalnutritionSeverityPerDay);
            course.Advance(span.End - Math.Max(span.Start, cursor), MalnutritionSeverityPerDay);
            cursor = Math.Max(cursor, span.End);
        }
        course.Advance(horizon - cursor, -MalnutritionSeverityPerDay);
        course.Report(pawn, findings);
        if (food.ZeroSpans.Count > 0) findings.Add("malnutrition_rate_pawn_factor_unobserved:" + pawn);
        return course.LostTicks / GameTime.TicksPerDay;
    }

    /// <summary>
    /// The expected cases times the capability-days one case loses: its severity starts at the def's initial severity and
    /// follows the SeverityPerDay comp until it is gone, the Disappears comp's mean ends it, or the horizon does. The case
    /// lands on one of the colony's pawns, unknown which, so it is the mean over them; it is counted from the horizon's
    /// start, as the meal that causes it is not placed.
    /// </summary>
    private static Fact<double> FoodPoisoningLoss(double cases, KeyValuePair<string, ForecastNeedsResult>[] pawns,
        Dictionary<string, NeedsForecastPawn> observed, HediffPenaltyTable table, double movingMin, double horizon, SortedSet<string> findings)
    {
        if (pawns.Length == 0) { findings.Add("food_poisoning_unassessed:no_pawns"); return default; }
        var length = table.DisappearsAfterTicks is { } gone ? Math.Min(horizon, (gone.Min + gone.Max) / 2) : horizon;
        double sum = 0;
        foreach (var (pawn, _) in pawns)
        {
            if (!observed.TryGetValue(pawn, out var seen) || Levels(seen) is not { } levels)
            { findings.Add("capacities_unobserved:" + pawn); return default; }
            // The pawn's own current poisoning, if any, is the stage its capacities already carry.
            var course = new Course(table, levels, movingMin, seen.FoodPoisoningSeverity.ValueOrDefault) { Severity = table.InitialSeverity };
            course.Advance(length, table.SeverityPerDay ?? 0);
            course.Report(pawn, findings);
            sum += course.LostTicks / GameTime.TicksPerDay;
        }
        return Known(cases * sum / pawns.Length);
    }

    /// <summary>The pawn's five observed capacity levels, in <see cref="Capacities"/> order; null when any is unobserved.</summary>
    private static double[]? Levels(NeedsForecastPawn pawn) =>
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
    /// </summary>
    private sealed class Course(HediffPenaltyTable table, double[] observed, double movingMin, double observedSeverity)
    {
        private readonly PenaltyStage? current = table.StageAt(observedSeverity);
        private bool pain;
        private bool lethal;

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
                var boundaries = table.Stages.Select(s => s.MinSeverity).Append(0);
                if (table.LethalSeverity is { } cap) boundaries = boundaries.Append(cap);
                foreach (var boundary in boundaries)
                {
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

        public void Report(string pawn, SortedSet<string> findings)
        {
            if (pain) findings.Add("hediff_pain_unpriced:" + table.Hediff);
            if (lethal) findings.Add("lethal_severity_reached:" + table.Hediff + ":" + pawn);
        }

        private void Piece(double ticks, double from, double to)
        {
            if (table.StageAt((from + to) / 2) is not { } stage) return;
            pain |= stage.PainOffset > 0;
            LostTicks += (1 - Factor(stage)) * ticks;
        }

        private double Factor(PenaltyStage stage)
        {
            var after = new double[Capacities.Length];
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

        private bool Downed(double[] levels) => levels[0] < AwakeConsciousness || levels[1] <= movingMin;

        /// <summary>A stage's effect on one capacity as the game applies it: the offsets summed, the post factors multiplied, the smallest ceiling.</summary>
        private static (double Offset, double Post, double Max) Modifiers(PenaltyStage? stage, string capacity)
        {
            double offset = 0, post = 1, max = double.PositiveInfinity;
            foreach (var m in stage?.Modifiers ?? [])
            {
                if (m.Capacity != capacity) continue;
                offset += m.Offset;
                post *= m.PostFactor;
                max = Math.Min(max, m.SetMax ?? double.PositiveInfinity);
            }
            return (offset, post, max);
        }
    }
}
