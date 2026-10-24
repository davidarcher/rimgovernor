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
    public double GoalWorth(Importance importance, int? deadlineDay, long tick) => GoalWorthOn(importance, deadlineDay, tick / GameTime.TicksPerDay);

    /// <summary>A goal's worth if it is completed on <paramref name="day"/> (game days since the game began, fractions kept): the same curve as <see cref="GoalWorth"/>, which tier 2 reads at its modelled completion day (#3505).</summary>
    public double GoalWorthOn(Importance importance, int? deadlineDay, double day)
    {
        var late = deadlineDay is { } due ? day - due : 0;
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

    /// <summary>Capability lost to exhaustion collapse: the expected time a pawn awake at zero rest spends asleep on the ground (#3598). No rest category changes a capacity.</summary>
    public Fact<double> Rest { get; init; }

    /// <summary>Meal nutrition forgone by eating cookable ingredients raw (#3626): each raw eat's level gain times the cooked
    /// ratio less one, in days of the eater's food fall, one food-day per capability-day. Known, 0 with no raw eat.</summary>
    public Fact<double> RawIngredients { get; init; } = Known(0d);

    /// <summary>Expected food-poisoning cases times the capacity one case loses over its course, clipped to the horizon.</summary>
    public Fact<double> FoodPoisoning { get; init; }

    /// <summary>Capacity lost to hypothermia and heatstroke over the severity the stationary thermal spans give them, and the remaining horizon plus the terminal value when it reaches lethal.</summary>
    public Fact<double> Thermal { get; init; }

    /// <summary>Mental-break risk times the mean days a break of its tier takes the colonist out of work.</summary>
    public Fact<double> MoodBreaks { get; init; }

    /// <summary>The wellbeing weight times the mood-days lost against the baseline outcome.</summary>
    public Fact<double> Wellbeing { get; init; }

    /// <summary>
    /// Raid harm and death (harm model, #3471): raids per day over the horizon times the risk-transformed loss of one raid, summed
    /// over the pawns. Unknown, with the specific finding, when the schedule, composition, exposure cell, outfits or care are.
    /// </summary>
    public Fact<double> Harm { get; init; }

    /// <summary>The per-raid numbers behind <see cref="Harm"/>; null when it is unknown.</summary>
    public HarmDetail? HarmDetail { get; init; }

    /// <summary>What the harm term found unassessed or qualified (a subset of <see cref="Findings"/>).</summary>
    public EquatableArray<string> HarmFindings { get; init; }

    /// <summary>
    /// Capability added after completion, in tier 2's marginal values (#3531, #3636): negative (a gain) by the progress the outcome
    /// schedules on projects and the skill XP its work applies, positive by the medicine and the priced rooms' construction
    /// material it consumes (#3653); the sum of
    /// <see cref="InvestmentContributions"/>, 0 with none.
    /// </summary>
    public Fact<double> Investment { get; init; }

    /// <summary>Each allocation's rows behind <see cref="Investment"/>: the tier-2 key, the units moved and their loss.</summary>
    public EquatableArray<ForecastInvestmentContribution> InvestmentContributions { get; init; }

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
        foreach (var (mine, theirs) in new[] { (Hunger, other.Hunger), (Rest, other.Rest), (RawIngredients, other.RawIngredients), (FoodPoisoning, other.FoodPoisoning),
            (Thermal, other.Thermal), (MoodBreaks, other.MoodBreaks), (Wellbeing, other.Wellbeing), (Harm, other.Harm), (Investment, other.Investment), (Goals, other.Goals) })
        {
            if (!mine.TryGetValue(out var a) || !theirs.TryGetValue(out var b)) continue;
            delta += a - b;
            shared = true;
        }
        return shared ? Known(delta) : default;
    }
}

/// <summary>One row of an outcome's Investment term (#3636): what an allocation moves of a tier-2 key (project progress, applied skill
/// XP, medicine consumed) or of a construction material (#3653) and its loss in capability-days, negative for a gain.</summary>
public sealed record ForecastInvestmentContribution(string Activity, string Pawn, string Key, double Units, double Value);

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

    /// <summary>Expected raid deaths per day over the reference outcome's, beyond <see cref="ForecastValuePolicy.DeathRiskFloor"/> (#3546): the excess is the overshoot. Unassessed when the outcome's or the reference's harm is unknown; it then passes.</summary>
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
    internal const double MalnutritionSeverityPerDay = 0.453;

    /// <summary>One observed pawn as the ledger reads it: the row, and its five capacity levels (null when any is unobserved).</summary>
    private sealed record ObservedPawn(NeedsForecastPawn Pawn, double[]? Levels);

    /// <summary>A search prices thousands of outcomes against one context, so what the ledger reads from it is indexed once (#3468):
    /// its pawns by id with their capacity levels, and which penalty tables are valid.</summary>
    private sealed class LedgerIndex(ForecastCoupledContext context)
    {
        public readonly Dictionary<string, ObservedPawn> Pawns = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new ObservedPawn(g.First(), Levels(g.First())), StringComparer.Ordinal);
        public readonly Dictionary<string, NeedsForecastPawn> Rows = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        /// <summary>A pawn's raid distributions by what they depend on (#3546): outcomes that share a pawn's outfit and care share one.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> Harm = new();
        /// <summary>The food need tasks eating a cookable ingredient raw (#3626), by (task, pawn).</summary>
        public readonly Dictionary<(string, string), ForecastNeedTask> RawEats = context.NeedTasks
            .Where(n => n.Kind == "food" && context.CookedNutritionRatio.ContainsKey(n.Resource))
            .GroupBy(n => (n.Task, n.Pawn)).ToDictionary(g => g.Key, g => g.First());
        public readonly Dictionary<string, ForecastActivity> Activities = context.Activities.Activities.GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        /// <summary>The activities whose consumed inputs are charged as construction material (#3653).</summary>
        public readonly HashSet<string> MaterialCharged = ChargedMaterialActivities(context);
        /// <summary>The fueled sources with a known burn (#3673) of each shelter (#3679), by room.</summary>
        public readonly Dictionary<ForecastShelterId, ThermalSource[]> Fueled = FueledShelters(context)
            .GroupBy(s => s.Shelter.Room).ToDictionary(g => g.Key, g => g.Select(s => s.Source).DistinctBy(s => s.Target).ToArray());
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
        var index = Indexes.GetValue(context, static c => new LedgerIndex(c));
        return Ledger(outcome, context, index, horizon, baseline, HarmLedger.Of(outcome, context, horizon, index.Harm, index.Rows, commitments),
            GoalContributionsOf(ProgressOf(outcome, context, index), context, commitments ?? []));
    }

    /// <summary>
    /// The ledger's remaining terms around a harm term and goal contributions already priced (#3723): the comparison reads the
    /// harm term's death floor and the goals' routes before it needs these, so a staged value (<see cref="Stage"/>) prices them
    /// only when a comparison reaches the ledger. <see cref="Of"/> prices everything at once through this same assembly, so the
    /// two paths produce one ledger.
    /// </summary>
    private static ForecastValueResult Ledger(ForecastCoupledOutcome outcome, ForecastCoupledContext context, LedgerIndex index, double horizon,
        ForecastCoupledOutcome? baseline, HarmTerm harmTerm, List<ForecastGoalContribution> contributions)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var pawns = outcome.Needs.ToArray();
        Array.Sort(pawns, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
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
            double sum = 0;
            var known = true;
            foreach (var (pawn, result) in pawns)
            {
                if (RestLoss(pawn, result, index.Rows.GetValueOrDefault(pawn), horizon, findings) is { } lost) sum += lost;
                else known = false;
            }
            rest = known ? Known(sum) : default;
        }

        var rawIngredients = RawIngredientLoss(outcome, context, index, findings);

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

        findings.UnionWith(harmTerm.Findings);
        var harm = harmTerm.Loss;
        double earned = 0;
        foreach (var contribution in contributions) earned += contribution.Value;
        var goals = Known(-earned);
        var investmentRows = InvestmentOf(outcome, context, index, horizon, findings);
        PlannedRoomCredits.Credit(outcome, context, horizon, investmentRows, findings);
        double invested = 0;
        foreach (var row in investmentRows) invested += row.Value;
        var investment = Known(invested);
        var total = hunger.IsKnown && rest.IsKnown && rawIngredients.IsKnown && poisoning.IsKnown && thermal.IsKnown && breaks.IsKnown && wellbeing.IsKnown && harm.IsKnown
            ? Known(hunger.Value + rest.Value + rawIngredients.Value + poisoning.Value + thermal.Value + breaks.Value + wellbeing.Value + harm.Value + goals.Value + investment.Value) : default;
        if (!total.IsKnown) findings.Add("capability_value_unassessed");
        return new(total, EquatableArray.From(findings))
        {
            Hunger = hunger, Rest = rest, RawIngredients = rawIngredients, FoodPoisoning = poisoning, Thermal = thermal, MoodBreaks = breaks, Wellbeing = wellbeing,
            Harm = harm, HarmDetail = harmTerm.Detail, HarmFindings = EquatableArray.From(harmTerm.Findings), Investment = investment, InvestmentContributions = EquatableArray.From(investmentRows), Goals = goals, GoalContributions = EquatableArray.From(contributions),
        };
    }

    /// <summary>
    /// The meal nutrition the outcome's raw eats of cookable ingredients forgo (#3626), in capability-days: per selected food
    /// need task whose resource has a cooked ratio, its level gain times the ratio less one, over the eater's observed food fall
    /// per day. Unknown, with a finding, when a raw eater's food fall is unobserved.
    /// </summary>
    private static Fact<double> RawIngredientLoss(ForecastCoupledOutcome outcome, ForecastCoupledContext context, LedgerIndex index, SortedSet<string> findings)
    {
        if (index.RawEats.Count == 0) return Known(0d);
        double sum = 0;
        var known = true;
        foreach (var allocation in outcome.Plan.Allocations)
        {
            if (!index.RawEats.TryGetValue((allocation.Activity, allocation.Pawn), out var eat) || !eat.Gain.TryGetValue(out var gain)) continue;
            NeedRate? food = null;
            if (index.Rows.TryGetValue(allocation.Pawn, out var row)) foreach (var need in row.Needs) if (need.Kind == "food") { food = need; break; }
            if (food is null || !food.FallPerTick.TryGetValue(out var fall) || fall <= 0)
            { findings.Add("raw_ingredient_loss_unassessed:" + allocation.Pawn); known = false; continue; }
            sum += gain * (context.CookedNutritionRatio[eat.Resource] - 1) / (fall * GameTime.TicksPerDay);
        }
        return known ? Known(sum) : default;
    }

    /// <summary>Per ingredient the observed conversions cook, the best cooked over raw nutrition per unit, kept above 1 (#3626).
    /// An ingredient or product without a catalog Nutrition stat is named in <paramref name="findings"/>.</summary>
    internal static IReadOnlyDictionary<Resource, double> CookedNutritionRatios(IEnumerable<CoupledFoodConversion> conversions,
        IReadOnlyDictionary<Resource, double> nutrition, ISet<string> findings)
    {
        var ratios = new Dictionary<Resource, double>();
        foreach (var conversion in conversions)
        {
            if (conversion.IngredientCount <= 0) continue;
            if (!nutrition.TryGetValue(new(conversion.Ingredient), out var raw) || raw <= 0 || !nutrition.TryGetValue(new(conversion.Product), out var meal))
            { findings.Add("cooked_nutrition_unread:" + conversion.Ingredient + ":" + conversion.Product); continue; }
            var ratio = meal * conversion.ProductCount / conversion.IngredientCount / raw;
            if (ratio > 1 && ratio > ratios.GetValueOrDefault(new(conversion.Ingredient))) ratios[new(conversion.Ingredient)] = ratio;
        }
        return ratios;
    }

    /// <summary>The progress the outcome's scheduled work makes on each project (research points, cells sown), by <see cref="ForecastProjectProgress.Project"/>.</summary>
    private static Dictionary<string, double> ProgressOf(ForecastCoupledOutcome outcome, ForecastCoupledContext context, LedgerIndex index)
    {
        var progress = new Dictionary<string, double>(StringComparer.Ordinal);
        if (context.GoalValues.Count == 0) return progress;
        foreach (var allocation in outcome.Plan.Allocations)
            if (index.Activities.GetValueOrDefault(allocation.Activity)?.ProjectProgress is { } work)
                progress[work.Project] = progress.GetValueOrDefault(work.Project) + (allocation.Learning?.WorkAmount ?? allocation.WorkTicks * work.PointsPerTick);
        return progress;
    }

    /// <summary>
    /// The rows of the Investment term, per allocation and tier-2 key it moves (<see cref="ForecastCoupledContext.Investment"/>), as a
    /// loss: project progress (research points, cells sown) and the XP its learning callback applies to the pawn's skill stock
    /// (<see cref="ProjectGraph.SkillKey"/>; #3636) are gains, negative; each medicine kind it consumes (<see cref="ProjectGraph.MedicineKey"/>)
    /// is charged, positive. XP is what <see cref="ForecastSkillPolicy.ForecastSkill"/> applies after the learning factors and saturation,
    /// the unit tier 2 perturbs the stock by. A key tier 2 did not value adds no row. The construction material a charged
    /// activity consumes (<see cref="ChargedMaterialActivities"/>) is charged, positive, under <see cref="MaterialKey"/> at
    /// <see cref="ForecastCoupledContext.MaterialValues"/> (#3653): one priced at market value is named
    /// material_value_market, one unpriced is named material_value_unpriced and adds no row. Each fueled source of a shelter whose
    /// shell the outcome completes inside the horizon (<see cref="FueledShelters"/>, #3673, #3679) is charged its burn from then,
    /// FuelPerTick × (horizon − complete) units of its fuel, the same way, on a row of that source with no pawn. A served priced room's credit
    /// (<see cref="PlannedRoomCredits.Credit"/>, #3655, #3796) is added beside these rows.
    /// </summary>
    private static List<ForecastInvestmentContribution> InvestmentOf(ForecastCoupledOutcome outcome, ForecastCoupledContext context, LedgerIndex index,
        double horizon, SortedSet<string> findings)
    {
        var rows = new List<ForecastInvestmentContribution>();
        void Add(ForecastActivityAllocation allocation, string key, double units, double sign)
        {
            if (units > 0 && context.Investment.TryGetValue(key, out var perUnit)) rows.Add(new(allocation.Activity, allocation.Pawn, key, units, sign * units * perUnit));
        }
        void Material(string activity, string pawn, Resource def, double units)
        {
            if (units <= 0) return;
            var value = context.MaterialValues.GetValueOrDefault(def);
            if (value is null || value.Basis == MaterialValueBasis.Unpriced) { findings.Add("material_value_unpriced:" + def); return; }
            if (value.Basis == MaterialValueBasis.Market) findings.Add("material_value_market:" + def);
            rows.Add(new(activity, pawn, MaterialKey(def), units, units * value.PerUnit));
        }

        if (index.Fueled.Count != 0)
            foreach (var shelter in outcome.Shelters)
                if (index.Fueled.TryGetValue(shelter.Room, out var fueled) && shelter.CompleteAt.TryGetValue(out var complete))
                    foreach (var source in fueled) Material(source.Target, "", new(source.Fuel), source.FuelPerTick.Value * (horizon - complete));

        foreach (var allocation in outcome.Plan.Allocations)
        {
            var activity = index.Activities.GetValueOrDefault(allocation.Activity);
            if (activity?.ProjectProgress is { } work) Add(allocation, work.Project, (allocation.Learning?.WorkAmount ?? allocation.WorkTicks * work.PointsPerTick), -1);
            if (allocation.Learning is { } learning && learning.After.AppliedGain.TryGetValue(out var after) && learning.Before.AppliedGain.TryGetValue(out var before))
                Add(allocation, ProjectGraph.SkillKey(allocation.Pawn, learning.Skill), after - before, -1);
            if (activity is null) continue;
            var charged = index.MaterialCharged.Contains(allocation.Activity);
            foreach (var input in activity.Inputs)
            {
                if (!input.Units.TryGetValue(out var units)) continue;
                Add(allocation, ProjectGraph.MedicineKey(input.Resource), units, 1);
                if (charged) Material(allocation.Activity, allocation.Pawn, input.Resource, units);
            }
        }

        return rows;
    }

    /// <summary>
    /// What the outcome earns of the player's goals (#3503). Research: the points its scheduled work at a goal's project adds,
    /// at the goal's share per point. Relief: each requirement relief a selected commitment carries that a goal injected, once
    /// per relief need, at the goal's share. A goal's progress is capped at the whole goal; its value is the progress times
    /// the goal's worth at the observed tick, which carries the deadline's decay.
    /// </summary>
    private static List<ForecastGoalContribution> GoalContributionsOf(Dictionary<string, double> progress, ForecastCoupledContext context,
        IEnumerable<ForecastCommitment> commitments)
    {
        var result = new List<ForecastGoalContribution>();
        if (context.GoalValues.Count == 0) return result;
        var relief = commitments.Where(c => c.Outcome?.Requirements is not null).SelectMany(c => c.Outcome!.Requirements!.Relief)
            .GroupBy(r => r.Need, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Math.Min(1, g.Sum(r => r.Fraction)), StringComparer.Ordinal);
        foreach (var goal in context.GoalValues)
        {
            var share = (goal.Priced ? 0 : goal.Research.Sum(r => progress.GetValueOrDefault(r.Project) * r.SharePerPoint))
                + goal.Relief.Sum(r => relief.GetValueOrDefault(r.Need) * r.Share);
            share = Math.Min(1, share);
            var worth = context.Value.GoalWorth(goal.Importance, goal.DeadlineDay, context.Needs.Tick);
            // A priced goal's progress is paid at tier 2's marginal value per unit, which already carries the deadline's decay at the modelled day.
            var priced = goal.Units.Sum(u => progress.GetValueOrDefault(u.Project) * u.Value);
            var value = (share * worth) + priced;
            if (value <= 0) continue;
            result.Add(new(goal.Goal, worth > 0 ? value / worth : 0, worth, value));
        }
        return result;
    }

    /// <summary>The ledger and the floors of one outcome, as the comparison reads them (#3522), priced at once. Wellbeing is against <paramref name="reference"/>, the same for every outcome of a search.</summary>
    public static ForecastValueExplanation Explain(ForecastCoupledOutcome outcome, Fact<double> urgentZeroTicks, ForecastCoupledContext context,
        double horizon, ForecastCoupledOutcome reference, IEnumerable<ForecastCommitment>? commitments = null)
        => Stage(outcome, urgentZeroTicks, context, horizon, reference, ReferenceHarm(reference, context, horizon), commitments).Explanation;

    /// <summary>The reference outcome's harm detail the death floor is measured from (worn outfits, its own care; #3546); null when its
    /// harm is unknown. A search prices every outcome against one reference, so it reads this once (#3723).</summary>
    internal static HarmDetail? ReferenceHarm(ForecastCoupledOutcome reference, ForecastCoupledContext context, double horizon)
    {
        var index = Indexes.GetValue(context, static c => new LedgerIndex(c));
        return HarmLedger.Of(reference, context, horizon, index.Harm, index.Rows, null).Detail;
    }

    /// <summary>One outcome's value in the comparison's order (#3723): the floors and what they need now, the rest of the ledger when it is read.</summary>
    internal static Staged Stage(ForecastCoupledOutcome outcome, Fact<double> urgentZeroTicks, ForecastCoupledContext context, double horizon,
        ForecastCoupledOutcome reference, HarmDetail? referenceHarm, IEnumerable<ForecastCommitment>? commitments)
        => new(outcome, urgentZeroTicks, context, horizon, reference, referenceHarm, commitments);

    /// <summary>
    /// One outcome's value as a lazy comparison reads it (#3723). Construction prices what the comparison's first stages read: the
    /// urgent-zero floor from the timelines' zero time, the break-risk floor from the pawns' mood, the harm term and from it the
    /// death floor against the reference's harm, and the goal contributions the goal routes read. The remaining ledger terms
    /// (hunger, rest, raw ingredients, poisoning, thermal, breaks, wellbeing, investment) are priced once, on the first read of
    /// <see cref="Explanation"/>, through the same assembly as <see cref="Of"/>; a comparison the floors do not decide reads it on
    /// both sides, so a skipped term never shrinks the set of terms the ledger compares.
    /// </summary>
    internal sealed class Staged
    {
        private readonly ForecastCoupledOutcome outcome;
        private readonly ForecastCoupledContext context;
        private readonly LedgerIndex index;
        private readonly double horizon;
        private readonly ForecastCoupledOutcome reference;
        private readonly HarmTerm harm;
        private readonly List<ForecastGoalContribution> contributions;
        private ForecastValueExplanation? explanation;

        internal Staged(ForecastCoupledOutcome outcome, Fact<double> urgentZeroTicks, ForecastCoupledContext context, double horizon,
            ForecastCoupledOutcome reference, HarmDetail? referenceHarm, IEnumerable<ForecastCommitment>? commitments)
        {
            this.outcome = outcome; this.context = context; this.horizon = horizon; this.reference = reference;
            index = Indexes.GetValue(context, static c => new LedgerIndex(c));
            var policy = context.Value;
            var days = horizon / GameTime.TicksPerDay;
            var worst = 0d;
            var assessed = days > 0 && outcome.Needs.Count > 0;
            foreach (var result in outcome.Needs.Values)
            {
                if (result.Mood is { BreakRisk: { IsKnown: true } risk } && days > 0) worst = Math.Max(worst, risk.Value / days - policy.BreakRiskCap);
                else assessed = false;
            }
            harm = HarmLedger.Of(outcome, context, horizon, index.Harm, index.Rows, commitments);
            contributions = GoalContributionsOf(ProgressOf(outcome, context, index), context, commitments ?? []);
            UrgentZero = new(urgentZeroTicks.IsKnown, urgentZeroTicks.IsKnown ? Math.Max(0, urgentZeroTicks.Value) : 0);
            BreakRisk = new(assessed, worst);
            // The death floor: expected deaths a day above the reference's (worn outfits, its own care), beyond what the policy allows.
            Death = harm.Detail is { } detail && referenceHarm is { } before
                ? new(true, Math.Max(0, detail.DeathsPerDay - before.DeathsPerDay - policy.DeathRiskFloor)) : new(false, 0);
        }

        public ForecastValuePolicy Policy => context.Value;
        public ForecastFloor UrgentZero { get; }
        public ForecastFloor BreakRisk { get; }
        public ForecastFloor Death { get; }
        /// <summary>The goals the outcome progresses, as <see cref="ForecastValueResult.GoalContributions"/> will carry them.</summary>
        public IReadOnlyList<ForecastGoalContribution> GoalContributions => contributions;
        /// <summary>Whether the remaining ledger terms have been priced.</summary>
        public bool LedgerPriced => explanation is not null;
        /// <summary>The complete explanation, pricing the remaining ledger terms on the first read.</summary>
        public ForecastValueExplanation Explanation => explanation ??= new(Ledger(outcome, context, index, horizon, reference, harm, contributions), Policy)
            { UrgentZero = UrgentZero, BreakRisk = BreakRisk, Death = Death };
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

    /// <summary>
    /// A pawn's capability-days lost to exhaustion collapse (#3598). No rest category changes a capacity, but a pawn awake at
    /// zero rest falls asleep where it stands (Need_Rest.ShouldInvoluntarySleepFromMTB): each 150-tick need interval after its
    /// first 1000 ticks at zero it collapses with chance 150 / (MTB × a day), the MTB falling with the time at zero. Collapsed, it
    /// sleeps on the ground until the wake threshold, a whole loss for that time, clipped to the horizon. The time at zero restarts
    /// with each zero span, and a collapse ends the walk (the sleep refills the need). Null when the rest timeline, the wake
    /// threshold or the ground gain is unobserved.
    /// </summary>
    private static double? RestLoss(string pawn, ForecastNeedsResult result, NeedsForecastPawn? row, double horizon, SortedSet<string> findings)
    {
        var rest = Need(result, "rest");
        if (rest is null) return 0;
        if (!rest.ZeroTicks.IsKnown) { findings.Add("rest_unassessed:" + pawn); return null; }
        var spans = rest.ZeroSpans;
        if (spans.Count == 0) return 0;
        NeedRate? rate = null;
        if (row is not null) foreach (var need in row.Needs) if (need.Kind == "rest") { rate = need; break; }
        if (row is null || rate is null || !rate.UnservedGainPerTick.TryGetValue(out var gain) || gain <= 0 || !row.WakeThreshold.TryGetValue(out var wake))
        { findings.Add("rest_collapse_unassessed:" + pawn); return null; }
        var asleep = wake / gain;
        Span<int> order = spans.Count <= 64 ? stackalloc int[spans.Count] : new int[spans.Count];
        ByStart(spans, order);
        double awake = 1, lost = 0;
        foreach (var i in order)
        {
            var span = spans[i];
            // The pawn's time at zero before the review is not observed: a span open at the review counts from 0.
            if (span.Start <= 0) findings.Add("rest_ticks_at_zero_unobserved:" + pawn);
            for (var atZero = RestInterval; atZero <= span.End - span.Start; atZero += RestInterval)
            {
                if (InvoluntarySleepMtbDays(atZero) is not { } mtb) continue;
                var collapse = RestInterval / (mtb * GameTime.TicksPerDay);
                lost += awake * collapse * Math.Min(asleep, horizon - (span.Start + atZero));
                awake *= 1 - collapse;
            }
        }
        return lost / GameTime.TicksPerDay;
    }

    // Need.NeedInterval runs every 150 ticks; Need_Rest counts the time at zero and draws the collapse there.
    private const double RestInterval = 150;

    /// <summary>Need_Rest.ShouldInvoluntarySleepFromMTB: no collapse in the first 1000 ticks at zero, then an MTB of 0.25, 0.125, 1/12 and 0.0625 days, stepping at 15000, 30000 and 45000 ticks (a quarter, half and three quarters of a day).</summary>
    private static double? InvoluntarySleepMtbDays(double ticksAtZero) => ticksAtZero <= 1000 ? null
        : ticksAtZero < GameTime.TicksPerDay / 4 ? 0.25 : ticksAtZero < GameTime.TicksPerDay / 2 ? 0.125 : ticksAtZero < GameTime.TicksPerDay * 3 / 4 ? 1d / 12 : 0.0625;

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
