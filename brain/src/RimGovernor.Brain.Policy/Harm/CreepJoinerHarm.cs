using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The creepjoiner downside part of the harm term (#3844 D12, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll:
// - Firing: Pawn_CreepJoinerTracker.DoDownside (when CheckDownsideOccurs fires, see ForecastIsolation) sets triggeredDownside,
//   re-arms downsideTriggersAt and canTriggerDownsideAfter from the firing tick, calls HediffComp_ReplaceHediff.Trigger on each of
//   the downside's hediffs the pawn carries, then DownsideWorker.DoResponse. A repeating downside (repeats) fires again on the
//   re-armed timers.
// - PsychicAgony: CreepJoinerWorker_PsychicAgony.DoResponse pulses the creepjoiner and every spawned pawn within its Range.
// - Aggressive: CreepJoinerWorker_AggressiveDownside.DoResponse calls DoAggressive, which runs the creepjoiner's own aggressive
//   def's worker: CreepJoinerWorker_AggressionBasic sets its faction to the entities with an AssaultColony lord,
//   _FleshbeastEmergence turns it into a fleshbeast, _SightstealerAttack sets its faction and calls sightstealers. Each leaves the
//   colony a fight and one pawn fewer.
// - Leaves: CreepJoinerWorker_DepartDownside.DoResponse calls DoLeave: SetFaction(null) and an ExitMapBest lord.
// - Hediffs: HediffCompProperties_ReplaceHediff.TriggeredHediff.ApplyTo runs HediffGiverUtility.TryApply(partsToAffect,
//   countRange.RandomInRange, useCoverage: false): each of the count picks one part uniformly among the not-missing parts of the
//   defs without the hediff or a directly added part, so of n open parts each is struck with chance E[min(k, n)] / n; each struck
//   part's severity is set from severityRange (unless zero). A hediff whose own ReplaceHediff is not manual becomes its replacement
//   once its severity reaches the comp's severity (CompPostTickInterval).
// - Isolation: Pawn_PlayerSettings.RespectsAllowedArea is false for a pawn with a lord, so the hostile and the leaving creepjoiner
//   leave the isolation area; the hediffs act on the creepjoiner alone. Only the agony pulse depends on who stands near, which
//   ForecastIsolation credits.

/// <summary>A colonist creepjoiner whose downside has not shown (#3844): its pawn, the days since it joined and its kindDef.combatPower.</summary>
public sealed record UnshownCreepJoiner(string Pawn, double DaysJoined, double CombatPower);

/// <summary>
/// What the creepjoiner part of the harm term reads (#3844 D12): the creepjoiners whose downside has not shown, the ones whose
/// downside has (named, not priced again), the catalog's downside table, and the pawn values of the latest review that valued
/// pawns (#3825, the source #3857 uses before a selection). <see cref="Unread"/> says why the census is unread, which the term
/// raises as a required fact. The default is a read colony with no creepjoiner.
/// </summary>
public sealed record CreepJoinerHarmInputs
{
    public EquatableArray<UnshownCreepJoiner> Unshown { get; init; }

    public EquatableArray<string> Shown { get; init; }

    /// <summary>Why the creepjoiner census is unread; "" when it was read.</summary>
    public string Unread { get; init; } = "";

    public CreepJoinerDownsideTable Table { get; init; } = new();

    /// <summary>The previous review's pawn values; a creepjoiner without one (before the first layout plan, after a restart, or one that joined since) awaits the plan.</summary>
    public EquatableArray<PawnValue> Values { get; init; }
}

/// <summary>One creepjoiner's part of the term (#3844): the expected loss of each downside kind, in capability-days over the horizon.</summary>
public sealed record CreepJoinerPawnTerm(string Pawn, double Agony, double Aggressive, double Leaves, double Hediffs)
{
    public double Loss => Agony + Aggressive + Leaves + Hediffs;
}

/// <summary>The creepjoiner part of the harm term (#3844): its loss in capability-days over the horizon, the deaths a day it causes and each creepjoiner's row.</summary>
public sealed record CreepJoinerTerm(double Loss, double DeathsPerDay, IReadOnlyList<CreepJoinerPawnTerm> Pawns);

/// <summary>
/// The creepjoiner downside part of the harm term (#3844 D12): per colonist creepjoiner whose downside has not shown, over the
/// game's draw given the days since it joined (D11, <see cref="ForecastIsolation.Survival"/>), the chance each drawable downside
/// first fires in each of <see cref="TimingBins"/> stretches of the horizon times what it costs from then to the horizon:
/// <list type="bullet">
/// <item>PsychicAgony: its pulses (the first and, for a repeating downside, the expected renewals on its re-armed timers, <see cref="Renewals"/>)
/// × Σ over the colonists, the creepjoiner with them, of one pulse's loss (<see cref="ForecastIsolation.PulseLoss"/>).</item>
/// <item>Aggressive: one fight at the creepjoiner's combatPower against the other colonists (<see cref="HarmLedger.FightLoss"/>, whole-horizon as a
/// raid's), plus the creepjoiner's pawn value lost from then (its faction leaves the colony).</item>
/// <item>Leaves: the creepjoiner's pawn value (<see cref="PawnValue.PerDay"/>, priced as is when negative) lost from then.</item>
/// <item>Triggered hediffs (CrumblingMind, OrganDecay): each hediff's course on the creepjoiner from then, priced as illness is
/// (<see cref="IllnessForecast.Course"/>) on each part it may strike at its chance; a chained replacement (CrumblingMind into
/// CrumbledMind) is walked through. A destroyed organ the autopilot replaces books its surgery (<see cref="Surgeries"/>).</item>
/// </list>
/// Isolation changes none of these but the agony pulse, which the isolation room credits (<see cref="ForecastIsolation"/>).
/// An unread census, a creepjoiner's unread facts or a fight the exposure model does not cover throws.
/// </summary>
public static class CreepJoinerHarm
{
    /// <summary>The stretches the horizon is cut into for a downside's first firing.</summary>
    internal const int TimingBins = 12;

    /// <summary>The finding of a creepjoiner valued by no previous review: its leaving costs nothing yet (#3857's awaiting_plan).</summary>
    public const string ValueAwaitingPlan = "creepjoiner_value_awaiting_plan";

    /// <summary>What the term leaves out, named with every term that prices a creepjoiner.</summary>
    public static readonly IReadOnlyList<string> NotModelled =
    [
        // The creepjoiner's own aggressive def (Assault, FleshbeastEmergence, SightstealerAttack) is priced as one fight at its combatPower (D12).
        "creepjoiner_aggressive_kind_unmodelled",
        // Every colonist stands in an unheld pulse's reach (D10's assumption).
        "creepjoiner_agony_reach_unmodelled",
        // CanDoResponse (an exit to leave by, a conscious pawn to pulse) and CheckDownsideOccurs's downed, unspawned and prisoner gates.
        "creepjoiner_trigger_conditions_unmodelled",
        // Each struck organ is priced alone: two lungs or two kidneys lost together are not.
        "creepjoiner_organ_joint_loss_unmodelled",
        // A finished surgical inspection, which finds an undiagnosed hediff, does not narrow the posterior.
        "creepjoiner_inspection_posterior_unmodelled",
        // A cure (a healer mech serum) for a triggered hediff is not priced.
        "creepjoiner_hediff_cures_unmodelled",
    ];

    // The outcome-free part of one creepjoiner's cost, cached per census, pawn row, care, policy, moving minimum and horizon.
    private sealed record PawnCost(double Pulses, double AggressiveChance, double AggressiveDays, double LeavesDays, double Hediffs, double HediffDeaths,
        IReadOnlyList<OrganReplacement> Replacements, IReadOnlyList<string> Findings);

    private static readonly ConditionalWeakTable<CreepJoinerHarmInputs, ConditionalWeakTable<NeedsForecastPawn, ConcurrentDictionary<(HarmConsequences.Care, ForecastValuePolicy, double, double, StockArrival), PawnCost>>> Cache = new();

    /// <summary>The term over <paramref name="ordered"/> pawns (the forecast's colonists); none without a colonist creepjoiner whose downside has not shown.</summary>
    internal static CreepJoinerTerm Term(string[] ordered, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange, EquatableArray<WeaponBin>), HarmDistribution> fights,
        IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments, SortedSet<string> findings)
    {
        var inputs = InputsOf(context);
        var members = ordered.ToHashSet(StringComparer.Ordinal);
        foreach (var shown in inputs.Shown.Where(members.Contains))
        {
            // A shown downside (a repeating pulse, a hediff already given) is in the pawn's own reads or not priced again.
            findings.Add("creepjoiner_shown_downside_unmodelled:" + shown);
        }

        var unshown = inputs.Unshown.Where(u => members.Contains(u.Pawn)).OrderBy(u => u.Pawn, StringComparer.Ordinal).ToArray();
        if (unshown.Length == 0)
        {
            return new(0, 0, []);
        }

        var days = horizon / GameTime.TicksPerDay;
        var care = HarmConsequences.CareOf(outcome.Support);
        var rows = new List<CreepJoinerPawnTerm>(unshown.Length);
        double deaths = 0;
        foreach (var u in unshown)
        {
            var cost = CostOf(inputs, u, Row(pawns, u.Pawn), care, context, days);
            findings.UnionWith(cost.Findings);
            var agony = 0.0;
            if (cost.Pulses > 0)
            {
                var pulse = ForecastIsolation.AgonyOf(inputs.Table);
                agony = cost.Pulses * ordered.Sum(p => ForecastIsolation.PulseLoss(Row(pawns, p), pulse));
            }

            var aggressive = 0.0;
            if (cost.AggressiveChance > 0)
            {
                var fight = HarmLedger.FightLoss(u.CombatPower, [.. ordered.Where(p => p != u.Pawn)], outcome, context, horizon, fights, pawns, commitments, findings);
                aggressive = cost.AggressiveChance * fight.Loss;
                deaths += days > 0 ? cost.AggressiveChance * fight.Deaths / days : 0;
            }

            var leaves = 0.0;
            if (cost.AggressiveDays != 0 || cost.LeavesDays != 0)
            {
                // Both turns take the creepjoiner out of the colony from the firing: its value is lost for the days left.
                if (inputs.Values.FirstOrDefault(v => v.Pawn == u.Pawn) is { } value)
                {
                    aggressive += cost.AggressiveDays * value.PerDay;
                    leaves = cost.LeavesDays * value.PerDay;
                }
                else
                {
                    findings.Add(ValueAwaitingPlan + ":" + u.Pawn);
                }
            }

            deaths += days > 0 ? cost.HediffDeaths / days : 0;
            rows.Add(new(u.Pawn, agony, aggressive, leaves, cost.Hediffs));
        }

        findings.UnionWith(NotModelled);
        return new(rows.Sum(r => r.Loss), deaths, rows);
    }

    /// <summary>
    /// The organ replacements <paramref name="pawn"/>'s downside may book over <paramref name="horizon"/> ticks given
    /// <paramref name="care"/>, each at its probability over the horizon (#3830's surgery-rate list, <see cref="ForecastHospital"/>);
    /// none for a pawn that is no creepjoiner with its downside unshown.
    /// </summary>
    internal static IReadOnlyList<OrganReplacement> Surgeries(ForecastCoupledContext context, NeedsForecastPawn pawn, HarmConsequences.Care care, double horizon)
    {
        var inputs = InputsOf(context);
        return inputs.Unshown.FirstOrDefault(u => u.Pawn == pawn.Pawn) is { } u
            ? CostOf(inputs, u, pawn, care, context, horizon / GameTime.TicksPerDay).Replacements
            : [];
    }

    private static CreepJoinerHarmInputs InputsOf(ForecastCoupledContext context)
    {
        var inputs = context.Harm.CreepJoiners;
        return inputs.Unread.Length == 0 ? inputs : throw new RequiredFactException("creepjoiner census: " + inputs.Unread);
    }

    private static NeedsForecastPawn Row(IReadOnlyDictionary<string, NeedsForecastPawn> pawns, string pawn) =>
        pawns.TryGetValue(pawn, out var row) ? row : throw new RequiredFactException("capacities_unobserved:" + pawn);

    private static PawnCost CostOf(CreepJoinerHarmInputs inputs, UnshownCreepJoiner u, NeedsForecastPawn row, HarmConsequences.Care care,
        ForecastCoupledContext context, double days)
    {
        var movingMin = context.CapacityPenalties.MovingMinForCapable;
        var key = (care, context.Value, movingMin, days, context.Stock);
        var byRow = Cache.GetValue(inputs, _ => new()).GetValue(row, _ => new());
        // A creepjoiner's census row and its pawn row are one pawn; the census's days joined are in the census key.
        return byRow.GetOrAdd(key, _ => Compute(inputs.Table, u, row, care, context.Value, movingMin, days, context.Stock));
    }

    private static PawnCost Compute(CreepJoinerDownsideTable table, UnshownCreepJoiner u, NeedsForecastPawn row, HarmConsequences.Care care,
        ForecastValuePolicy policy, double movingMin, double days, StockArrival stock)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        double pulses = 0, aggressive = 0, aggressiveDays = 0, leavesDays = 0, hediffs = 0, deaths = 0;
        var replacements = new Dictionary<(string, string, SurgeryWantReason), OrganReplacement>();
        foreach (var (d, chance, left) in Firings(table, u.DaysJoined, days))
        {
            findings.UnionWith(d.Unmodelled);
            switch (d.Response)
            {
                case CreepJoinerResponse.PsychicAgony:
                    pulses += chance * (1 + (d.Repeats ? Renewals(d, left) : 0));
                    break;
                case CreepJoinerResponse.Aggressive:
                    aggressive += chance;
                    aggressiveDays += chance * left;
                    break;
                case CreepJoinerResponse.Leaves:
                    leavesDays += chance * left;
                    break;
            }

            foreach (var hediff in d.Hediffs)
            {
                var (loss, died, lost) = HediffCost(hediff, row, care, policy, movingMin, left, stock, findings);
                hediffs += chance * loss;
                deaths += chance * died;
                foreach (var r in lost)
                {
                    var k = (r.Part, r.Recipe?.Recipe ?? "", r.Reason);
                    replacements[k] = r with { Probability = chance * r.Probability + (replacements.TryGetValue(k, out var seen) ? seen.Probability : 0) };
                }
            }
        }

        return new(pulses, aggressive, aggressiveDays, leavesDays, hediffs, deaths,
            [.. replacements.Values.OrderBy(r => r.Part, StringComparer.Ordinal).ThenBy(r => r.Recipe?.Recipe ?? "", StringComparer.Ordinal).ThenBy(r => r.Reason)], [.. findings]);
    }

    /// <summary>
    /// Each drawable downside that adds no trait, with the chance it is the creepjoiner's and first fires in one of
    /// <see cref="TimingBins"/> equal stretches of the next <paramref name="days"/> given it has not shown <paramref name="nowDays"/>
    /// after joining (w × (S(from) − S(to)) / Σ w × S(now), D11), and the days left to the horizon from the stretch's middle. The
    /// draw's requires and combat-point gates raise a required fact as the isolation credit's do.
    /// </summary>
    internal static IEnumerable<(CreepJoinerDownsideRow Row, double Chance, double Left)> Firings(CreepJoinerDownsideTable table, double nowDays, double days)
    {
        if (table.Unmirrored.Length != 0) throw new RequiredFactException("creepjoiner downside draw: " + table.Unmirrored);
        var drawn = new List<CreepJoinerDownsideRow>();
        foreach (var d in table.Downsides.Where(d => d.CanOccurRandomly))
        {
            if (d.MinCombatPoints > 0)
                throw new RequiredFactException(FormattableString.Invariant($"creepjoiner downside draw: the combat points {d.Def} needs ({d.MinCombatPoints}) at the draw"));
            if (!d.AddsTrait && d.Weight > 0) drawn.Add(d);
        }

        var hidden = drawn.Sum(d => d.Weight * ForecastIsolation.Survival(d, nowDays));
        if (!(hidden > 0) || !(days > 0)) yield break;
        foreach (var d in drawn)
        {
            for (var b = 0; b < TimingBins; b++)
            {
                var chance = d.Weight * Math.Max(0, ForecastIsolation.Survival(d, nowDays + days * b / TimingBins) - ForecastIsolation.Survival(d, nowDays + days * (b + 1) / TimingBins)) / hidden;
                if (chance > 0) yield return (d, chance, days * (1 - (b + 0.5) / TimingBins));
            }
        }
    }

    /// <summary>
    /// The expected firings of a repeating downside in the <paramref name="days"/> after one (the renewal function): DoDownside
    /// re-arms its timers from the firing, so each gap has the survival <see cref="ForecastIsolation.Survival"/> from 0, and
    /// M(t) = F(t) + ∫ M(t − x) dF(x), taken on the checks' quarter-day grid.
    /// </summary>
    internal static double Renewals(CreepJoinerDownsideRow d, double days)
    {
        var step = ForecastIsolation.CheckTicks / GameTime.TicksPerDay;
        var n = (int)Math.Floor(days / step + 1e-9);
        if (n <= 0) return 0;
        var gap = new double[n + 1];
        var renewals = new double[n + 1];
        for (var j = 1; j <= n; j++) gap[j] = Math.Max(0, ForecastIsolation.Survival(d, (j - 1) * step) - ForecastIsolation.Survival(d, j * step));
        for (var i = 1; i <= n; i++)
        {
            var m = 1 - ForecastIsolation.Survival(d, i * step);
            for (var j = 1; j <= i; j++) m += gap[j] * renewals[i - j];
            renewals[i] = m;
        }

        return renewals[n];
    }

    /// <summary>
    /// One triggered hediff's cost to <paramref name="row"/> over the <paramref name="days"/> left: its risked loss in
    /// capability-days, the chance it kills, and the organ replacements it books with their probabilities. A hediff of parts
    /// strikes each open part (the read's parts listing it) with chance E[min(k, n)] / n; its severity is drawn on the midpoint grid
    /// of its severity range. A chained hediff is walked through its replacement.
    /// </summary>
    private static (double Loss, double Died, List<OrganReplacement> Replacements) HediffCost(CreepJoinerHediff hediff, NeedsForecastPawn row,
        HarmConsequences.Care care, ForecastValuePolicy policy, double movingMin, double days, StockArrival stock, SortedSet<string> findings)
    {
        var course = hediff.Course;
        var name = course.Table.Hediff;
        if (!course.Valid()) throw new DomainException("the creepjoiner hediff " + name + " is not a course");
        foreach (var kind in course.Unmodelled) findings.Add("creepjoiner_hediff_unmodelled:" + name + ":" + kind);
        findings.UnionWith(course.Unpriced);
        var levels = ForecastValue.Levels(row);
        if (hediff.Then is not null)
        {
            if (course.Parts.Count != 0) throw new DomainException("the creepjoiner hediff chain from " + name + " strikes parts");
            return (Chain(hediff, Severities(hediff), levels, movingMin, days, row.Pawn, findings), 0, []);
        }

        var lethal = course.Table.Lethality is Lethality.Lethal;
        var replacements = new List<OrganReplacement>();
        double loss = 0, died = 0;
        void Price(IReadOnlyList<IllnessForecast.Branch> branches, double chance)
        {
            var points = new List<HarmPoint>();
            foreach (var (severity, weight) in Severities(hediff))
            {
                var at = course with { Table = course.Table with { InitialSeverity = severity } };
                foreach (var o in IllnessForecast.Course(at, branches, 1, care, levels, movingMin, policy, days, row.Pawn, findings))
                {
                    points.Add(new(o.Loss, weight * o.Probability));
                    died += o.Died ? chance * weight * o.Probability : 0;
                    if (o.Replacement is { } lost) replacements.Add(lost with { Probability = chance * weight * o.Probability });
                }
            }

            var (mean, tail) = HarmLedger.Moments(new HarmDistribution { Known = true, Points = points }, policy.HarmRiskAlpha);
            loss += chance * HarmLedger.Risk(mean, tail, policy.HarmRiskWeight);
        }

        if (course.Parts.Count == 0)
        {
            Price([new IllnessForecast.Branch(1, lethal, null)], 1);
            return (loss, died, replacements);
        }

        var disease = row.Disease;
        var defs = course.Parts.ToDictionary(p => p.Part, StringComparer.Ordinal);
        var rows = disease.Parts.Where(r => defs.ContainsKey(r.Part)).ToArray();
        if (rows.Length == 0) throw new RequiredFactException("creepjoiner_disease_parts:" + row.Pawn + ":" + name);
        var open = rows.Where(r => r.Hediffs.Contains(name)).ToArray();
        if (open.Length == 0) return (0, 0, []);
        // Each of the k picks takes one open part without the hediff: min(k, n) of the n parts, each alike.
        var (kMin, kMax) = hediff.Count;
        var struck = Enumerable.Range(kMin, Math.Max(0, kMax - kMin) + 1).Average(k => (double)Math.Min(k, open.Length)) / open.Length;
        foreach (var part in open)
        {
            var def = defs[part.Part];
            Price([new IllnessForecast.Branch(1, lethal && def.Threatened, IllnessForecast.Organ(course, def, part, rows, disease, levels, stock, days, findings))], struck);
        }

        return (loss, died, replacements);
    }

    // The severities a triggered hediff is set to: the midpoint grid of its range, or its initialSeverity for none.
    private static IEnumerable<(double Severity, double Weight)> Severities(CreepJoinerHediff hediff) =>
        hediff.Severity is (0, 0) ? [(hediff.Course.Table.InitialSeverity, 1)] : IllnessForecast.Grid(hediff.Severity.Min, hediff.Severity.Max);

    /// <summary>
    /// The capability-days a chained whole-body hediff costs over <paramref name="days"/>: from each starting severity, at each rate of
    /// its per-day grid, its stages until its severity reaches <see cref="CreepJoinerHediff.ThenAt"/> (or its maxSeverity, held), then its
    /// replacement's from that replacement's own start.
    /// </summary>
    private static double Chain(CreepJoinerHediff hediff, IEnumerable<(double Severity, double Weight)> starts, double[] levels, double movingMin, double days,
        string pawn, SortedSet<string> findings)
    {
        var course = hediff.Course;
        // A chained replacement names what its own course leaves out (CrumbledMind's disabled work).
        foreach (var kind in course.Unmodelled) findings.Add("creepjoiner_hediff_unmodelled:" + course.Table.Hediff + ":" + kind);
        findings.UnionWith(course.Unpriced);
        var top = hediff.Then is null ? course.MaxSeverity : Math.Min(hediff.ThenAt, course.MaxSeverity);
        var loss = 0.0;
        foreach (var (start, sw) in starts)
        {
            foreach (var (perDay, pw) in IllnessForecast.Grid(course.PerDayMin, course.PerDayMax))
            {
                var walk = new ForecastValue.Course(course.Table, levels, movingMin, 0) { Severity = start };
                var reach = perDay > 0 && double.IsFinite(top) ? Math.Max(0, (top - start) / perDay) : double.PositiveInfinity;
                var first = Math.Min(days, reach);
                walk.Advance(first * GameTime.TicksPerDay, perDay);
                var lost = walk.LostTicks / GameTime.TicksPerDay;
                if (days > reach)
                {
                    if (hediff.Then is { } then && hediff.ThenAt <= course.MaxSeverity)
                    {
                        lost += Chain(then, Severities(then), levels, movingMin, days - reach, pawn, findings);
                    }
                    else
                    {
                        // Held at its maxSeverity, where its own ReplaceHediff never fires.
                        walk.Advance((days - reach) * GameTime.TicksPerDay, 0);
                        lost = walk.LostTicks / GameTime.TicksPerDay;
                    }
                }

                walk.Report(pawn, findings);
                loss += sw * pw * Math.Min(days, lost);
            }
        }

        return loss;
    }
}
