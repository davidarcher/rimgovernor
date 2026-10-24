namespace RimGovernor.Brain.Policy;

/// <summary>Inspectable consequences of one outcome. The ranking reads <see cref="Value"/> (floors, then the capability-days ledger)
/// and then only the terms with no mechanical conversion yet: care and room factors, maintenance, joy exposure, development,
/// supply and first care. The other fields explain the outcome.</summary>
public sealed record ForecastConsequenceVector(EquatableArray<Fact<double>> Urgent, EquatableArray<Fact<double>> Needs,
    Fact<double> MoodExposure, Fact<double> MoodDepth, double Development, double Labor,
    EquatableArray<string> Unknown)
{
    /// <summary>The ledger, floors and constants this outcome was ranked by (#3522); empty when assembled without a context.</summary>
    public ForecastValueExplanation Value { get; init; } = ForecastValueExplanation.Empty;
    /// <summary>Expected mental breaks summed over pawns; known only when every pawn's break threshold and mood are.</summary>
    public Fact<double> BreakRisk { get; init; }
    public string ThermalOccupancy { get; init; } = "";
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public ForecastSupportConsequences Support { get; init; } = new();
}

public sealed partial record ForecastCoupledSelection(ForecastActivityOutcomeComparison Selection,
    ForecastCoupledOutcome Baseline, ForecastCoupledOutcome Selected,
    ForecastConsequenceVector BaselineVector, ForecastConsequenceVector SelectedVector,
    EquatableArray<ForecastPortfolioEvidence> Rejected, double Horizon, bool GenerationTruncated)
{
    public int CompositionExpansions { get; init; }
    public EquatableArray<ForecastServiceInvestment> ServiceInvestments { get; init; }
    /// <summary>The eligible colonists the selection left unassigned while their needs stand above every threshold: they run vanilla work
    /// (#3669), and a later review reconsiders them. While any stands, the admin/work-assignments Need is
    /// owed (<see cref="RoundsRules.WorkAssignmentsOwed"/>).</summary>
    public EquatableArray<string> Deferred { get; init; }
    /// <summary>The search's spend per phase it reached (#3563): baseline, warm, seed, unserved, extend, confirm, sweep; after a
    /// planned-unplaced reselection (#3557) the second comparison's phases follow as <c>reselect/&lt;phase&gt;</c>. The phases'
    /// expansions and outcome evaluations sum to <see cref="ForecastActivityOutcomeComparison.Expansions"/> and <see cref="OutcomeEvaluations"/>.</summary>
    public EquatableArray<ForecastSearchPhase> SearchPhases { get; init; }
    /// <summary>The phase in which the chosen portfolio first became the leader; "baseline" when nothing beat it.</summary>
    public string SelectedPhase { get; init; } = "";
    /// <summary>"" without a previous selection; "seeded" when the previous selection resolved to current roots and was evaluated first; "missing" when none of its ids resolved.</summary>
    public string WarmStart { get; init; } = "";
    /// <summary>The allowance the comparison spent against (#3722).</summary>
    public ForecastSearchBudget Budget { get; init; } = ForecastSearchBudget.Default;
    /// <summary>Outcome evaluations the whole comparison started (#3722), the baseline's included; the discretionary ones,
    /// <see cref="OutcomeEvaluations"/> less <see cref="BaselineEvaluations"/>, never exceed <see cref="ForecastSearchBudget.Evaluations"/>.</summary>
    public int OutcomeEvaluations { get; init; }
    /// <summary>The mandatory baseline's outcome evaluations (#3722): counted in <see cref="OutcomeEvaluations"/>, charged to no allowance.</summary>
    public int BaselineEvaluations { get; init; }
    /// <summary>The mandatory baseline's scheduler expansions (#3722): counted in <see cref="ForecastActivityOutcomeComparison.Expansions"/>, charged to no allowance.</summary>
    public int BaselineExpansions { get; init; }
    /// <summary>Why a bound was unavailable for some evaluated subset (#3724), as <c>&lt;bound&gt;:&lt;reason&gt;</c>, distinct and sorted: for
    /// <see cref="UrgentZeroUnservedBound"/>, <c>goal_routes</c> (the context values goals, whose routes every evaluated outcome
    /// reports), <c>baseline_zero_unassessed</c> (a pawn's baseline food or rest zero time is unknown), <c>leader_unassessed</c> (the
    /// leader is infeasible or its urgent-zero floor unassessed), <c>rest_service_in_closure</c> (a rest service in the subset's
    /// work could leave a pawn's rest unassessed in some plan) or <c>relief_gate_order</c> (a single construction root the relief
    /// gate must see). The evaluations so started are the phases' <see cref="ForecastSearchPhase.Unbounded"/>.</summary>
    public EquatableArray<string> BoundsUnavailable { get; init; }
}
/// <summary>
/// One phase the search reached: its scheduler expansions and, for the profile (#3556), its wall, the schedule
/// searches it ran (<see cref="ScheduleSearches"/>; a subset whose schedule was already searched at the bound runs none) with
/// their wall (<see cref="ScheduleMs"/>; the rest of <see cref="Ms"/> is keying, feasibility and ranking), the outcome
/// evaluations it started (<see cref="OutcomeEvaluations"/>, #3722), and the evaluations whose every outcome was infeasible or
/// unassessed (<see cref="Infeasible"/>) or whose construction lacked supported service relief (<see cref="Unsupported"/>), and
/// the pruning (#3724): the subsets and prefixes a proven bound skipped (<see cref="Pruned"/>) and the evaluations started
/// without a bound available (<see cref="Unbounded"/>).
/// </summary>
public sealed record ForecastSearchPhase(string Phase, int Expansions)
{
    public int ScheduleSearches { get; init; }
    public int OutcomeEvaluations { get; init; }
    public double Ms { get; init; }
    public double ScheduleMs { get; init; }
    public int Infeasible { get; init; }
    public int Unsupported { get; init; }
    /// <summary>Extension evaluations that swapped a root exclusive with a leader member in for it, rather than adding a compatible root.</summary>
    public int Swaps { get; init; }
    /// <summary>Evaluations that made a new leader.</summary>
    public int Improved { get; init; }
    /// <summary>Subsets, and in the sweep prefixes with every completion, a proven bound skipped without scheduling or evaluating (#3724).</summary>
    public int Pruned { get; init; }
    /// <summary>Evaluations started while no bound was available for the subset (<see cref="ForecastCoupledSelection.BoundsUnavailable"/> names why; #3724).</summary>
    public int Unbounded { get; init; }
}
/// <summary>
/// Why an evaluated subset is not the selection. <see cref="Reason"/> is <see cref="InfeasibleOrUnassessed"/> when none of its
/// outcomes was feasible, a <c>construction_without_supported_service_relief</c> finding when the thermal-relief gate held it
/// back, <see cref="LostToLeaderPrefix"/> followed by the comparator stage that proved it loses to the leader of its time
/// (#3723): <c>urgent_zero</c>, <c>death_risk_floor</c>, <c>break_risk_cap</c>, <c>ledger</c>, one of the unconverted terms, or
/// the caller's <c>incumbent</c> or <c>labor</c> rule, or <see cref="PrunedPrefix"/> (a subset) or <see cref="PrunedBranchPrefix"/>
/// (a sweep prefix, every completion of it) followed by the bound that proved every plan of it loses to the leader of its time
/// without scheduling it, and the pawn that proves it (#3724): <c>urgent_zero_unserved:&lt;pawn&gt;</c>. <see cref="Consequences"/>
/// is the outcome's complete vector when its ledger was priced, and null when the subset was never ranked, when a floor decided
/// before the ledger was needed or when a bound skipped it: a skipped evaluation is reported as unevaluated, never as a zero-valued vector.
/// </summary>
public sealed record ForecastPortfolioEvidence(EquatableArray<string> Commitments,
    ForecastConsequenceVector? Consequences, string Reason)
{
    public const string InfeasibleOrUnassessed = "infeasible_or_unassessed";
    public const string LostToLeaderPrefix = "lost_to_leader:";
    public const string PrunedPrefix = "pruned:";
    public const string PrunedBranchPrefix = "pruned_branch:";
    /// <summary>The comparator stage at which a subset's outcome was proven to lose to the leader, read from its <see cref="Reason"/>
    /// (#3723); null for a reason that is not a leader loss. The hook for a finalist collector (#3720): a candidate losing at a late
    /// stage may still be close. The <c>coupled_selection</c> row's <c>rejected[].reason</c> carries the same string.</summary>
    public static string? StageOf(string reason) => reason.StartsWith(LostToLeaderPrefix, StringComparison.Ordinal) ? reason[LostToLeaderPrefix.Length..] : null;
    /// <summary>The bound that skipped the subset (<see cref="PrunedPrefix"/>) or every completion of the prefix (<see cref="PrunedBranchPrefix"/>)
    /// without evaluating it (#3724), <see cref="ForecastCoupledSelection.UrgentZeroUnservedBound"/>; null for a reason that is not a pruning.</summary>
    public static string? BoundOf(string reason)
    {
        var start = reason.StartsWith(PrunedPrefix, StringComparison.Ordinal) ? PrunedPrefix.Length
            : reason.StartsWith(PrunedBranchPrefix, StringComparison.Ordinal) ? PrunedBranchPrefix.Length : -1;
        if (start < 0) return null;
        var end = reason.IndexOf(':', start);
        return end < 0 ? reason[start..] : reason[start..end];
    }
    /// <summary>Whether the subset was ranked on its consequences: feasible and either compared with the leader or held by the relief gate,
    /// or proven by a bound to lose to the leader (#3724). False only when none of its outcomes was feasible or assessed.</summary>
    public bool Assessed => Reason != InfeasibleOrUnassessed;
}

/// <summary>
/// The deterministic allowance of one portfolio comparison (#3722). <see cref="Evaluations"/> is the budget: outcome evaluations,
/// one per complete plan a schedule search hands to <c>CoupledSearchCache.Evaluate</c> (starting one spends the unit even when a
/// later stage stops early; a schedule or outcome reused from the search's cache spends none). <see cref="Traversal"/> is the bound
/// on scheduler expansions over the same comparison, so refused, duplicate, infeasible and unknown-heavy branches cannot do
/// unbounded work without evaluating an outcome. Both count algorithmic work, never wall-clock. The baseline is mandatory work,
/// counted and reported but charged to neither; every nested search and the planned-unplaced reselection draw from one allowance.
/// </summary>
public sealed record ForecastSearchBudget(int Evaluations, int Traversal = ForecastSearchBudget.DefaultTraversal)
{
    /// <summary>The production allowance (#3722; docs/developers/testing/portfolio-allocations.md; tuned by #3725). The former
    /// 65,536-expansion default spent 1,728 outcome evaluations on the four-worker fixture at the one-day horizon, 2,048 rounded up to a
    /// power of two; 8,192 is the smallest power of two at which every default-budget test keeps its selection and the cardinality
    /// sweep still runs on the cheap ten-root shelter graphs where the former default ran it (there it was worth about 13,000 evaluations).</summary>
    public const int DefaultEvaluations = 8192;
    /// <summary>The traversal bound: the former expansion default, which the converged search rarely reached.</summary>
    public const int DefaultTraversal = 65536;
    public static readonly ForecastSearchBudget Default = new(DefaultEvaluations);
}

/// <summary>One comparison's spending against its <see cref="ForecastSearchBudget"/> (#3722), shared by every nested schedule search
/// and both comparisons of a planned-unplaced reselection; nothing resets or extends it. While <see cref="Mandatory"/>, the spend is
/// the baseline's: counted and reported, charged to neither allowance.</summary>
internal sealed class ForecastSearchLedger(ForecastSearchBudget budget)
{
    public ForecastSearchBudget Budget { get; } = budget;
    /// <summary>Outcome evaluations started, the mandatory ones included.</summary>
    public int Evaluations { get; private set; }
    /// <summary>Scheduler expansions spent, the mandatory ones included.</summary>
    public int Expansions { get; private set; }
    public int BaselineEvaluations { get; private set; }
    public int BaselineExpansions { get; private set; }
    public bool Mandatory { get; set; }
    public int DiscretionaryEvaluations => Evaluations - BaselineEvaluations;
    public int DiscretionaryExpansions => Expansions - BaselineExpansions;
    public int EvaluationsLeft => Math.Max(0, Budget.Evaluations - DiscretionaryEvaluations);
    public int TraversalLeft => Math.Max(0, Budget.Traversal - DiscretionaryExpansions);
    public bool EvaluationsExhausted => EvaluationsLeft == 0;
    public bool TraversalExhausted => TraversalLeft == 0;
    public bool Exhausted => EvaluationsExhausted || TraversalExhausted;
    /// <summary>The scheduler bound of the next schedule search: <paramref name="bound"/> within the traversal left; 0 when none is left.</summary>
    public int SchedulerBound(int bound) => Mandatory ? bound : Math.Min(bound, TraversalLeft);
    /// <summary>Starts one outcome evaluation; false, spending nothing, when the allowance is spent.</summary>
    public bool TryStartEvaluation()
    {
        if (!Mandatory && EvaluationsExhausted) return false;
        Evaluations++;
        if (Mandatory) BaselineEvaluations++;
        return true;
    }
    public void SpendExpansions(int count)
    {
        Expansions += count;
        if (Mandatory) BaselineExpansions += count;
    }
}

public sealed partial record ForecastCoupledSelection
{
    private static readonly System.Diagnostics.ActivitySource Spans = new("github.com/davidarcher/RimGovernor/policy");

    /// <summary>A selection finding <c>goal_infeasible:&lt;goal need id&gt;:&lt;floor&gt;</c> (#3503): every evaluated route that progresses the goal breaks that floor where the baseline holds it.</summary>
    public const string GoalInfeasiblePrefix = "goal_infeasible:";

    /// <summary>The ExclusiveOutcome prefix of a placed site's construction root, before the site's thing id.</summary>
    internal const string ConstructionOutcome = "construction/";

    private const int SeedShare = 4;
    /// <summary>The urgent-zero floor is time at zero in game ticks, and needs advance in whole ticks: two excesses less than
    /// a tick apart are the same floor. Summed rates leave sub-tick residue that differs by plan (review 0 of
    /// production/coupled-needs, #3598: 39374.99978028881 against ...882 let a waking plan with 0.40 days more rest loss
    /// beat the plan that slept), so a residue never decides ahead of the ledger.</summary>
    internal const double UrgentZeroResolutionTicks = 1;
    /// <summary>Scheduler bound of a screening evaluation. The depth-first schedule search reaches its first complete
    /// plans within a few expansions per activity and spends the rest of a larger bound varying the last tasks, so a
    /// portfolio's consequences are ranked from the first few dozen plans; the 4,096 bound confirms the final choice.</summary>
    private const int ScreenBound = 64;
    private const int PortfolioBound = 4096;

    /// <summary>One bounded subset search, evaluated by the same ColonyForecast trajectory as its baseline.
    /// Empty/continue is evaluated first. Partial portfolios remain expandable regardless of isolated value.
    /// Order: single roots, screened, for up to a quarter of the budget, then each planned room alternative's site roots
    /// as one unit (#3647); the leader plus each other root, then plus each such unit, screened, repeated from each new leader until none improves it; the final leader at the full
    /// per-portfolio bound; then the full cardinality sweep (singles, pairs, ...) at that bound, skipping
    /// subsets already evaluated at it. <paramref name="warmStart"/>, the previous review's selected ids, is screened
    /// before the seed pass (#3563) so the extension starts from last review's leader when it still stands.
    /// A chosen portfolio of unplaced planned sites alone starts no order (#3278), so the comparison is run again
    /// without the roots that reach a planned site, and that run's selected roots, allocations and unselected-pawn
    /// defers join the selection (#3557): the record places the planned sites, the orders start the startable work,
    /// and vanilla work is the fallback only when nothing startable is worth selecting (#3669). Both comparisons spend one
    /// <paramref name="budget"/> (#3722; <see cref="ForecastSearchBudget.Default"/> when null) through one ledger.</summary>
    public static ForecastCoupledSelection CompareActivityOutcomes(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, ForecastSearchBudget? budget = null, IReadOnlyList<string>? warmStart = null, Func<double>? clockMs = null)
        => CompareActivityOutcomes(request, candidates, budget, warmStart, clockMs, eager: false, prune: true);

    /// <summary>The test control of lazy evaluation (#3723): <see cref="CompareActivityOutcomes(ColonyForecastRequest, ForecastCoupledCandidates, ForecastSearchBudget?, IReadOnlyList{string}?, Func{double}?)"/>
    /// with every evaluated outcome's ledger priced as soon as the outcome is, so a paired test can prove the lazy search returns the same
    /// selection and evidence. Production always runs lazily.</summary>
    internal static ForecastCoupledSelection CompareActivityOutcomesEagerly(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, ForecastSearchBudget? budget = null, IReadOnlyList<string>? warmStart = null)
        => CompareActivityOutcomes(request, candidates, budget, warmStart, null, eager: true, prune: true);

    /// <summary>The test control of pruning (#3724): the same search with every subset scheduled and evaluated, no bound applied, so a
    /// paired test can prove the pruned search selects what the exhaustive one selects while scheduling less. Production always prunes.</summary>
    internal static ForecastCoupledSelection CompareActivityOutcomesUnpruned(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, ForecastSearchBudget? budget = null, IReadOnlyList<string>? warmStart = null)
        => CompareActivityOutcomes(request, candidates, budget, warmStart, null, eager: false, prune: false);

    private static ForecastCoupledSelection CompareActivityOutcomes(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, ForecastSearchBudget? budget, IReadOnlyList<string>? warmStart, Func<double>? clockMs, bool eager, bool prune)
    {
        budget ??= ForecastSearchBudget.Default;
        if (budget.Evaluations < 0 || budget.Traversal < 0) throw new DomainException("negative portfolio budget");
        var ledger = new ForecastSearchLedger(budget);
        var selection = CompareOnce(request, candidates, ledger, warmStart, clockMs, eager, prune);
        var unplaced = candidates.Context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var allocations = selection.Selected.Plan.Allocations;
        if (unplaced.Count == 0 || allocations.Length == 0 || !allocations.All(a => unplaced.Contains(a.Activity))) return selection;
        var reaching = ReachingActivities(candidates.Context.Activities.Activities, unplaced);
        var placed = candidates with { Alternatives = EquatableArray.From(candidates.Alternatives.Where(c => !c.ActivityCompletions.Any(reaching.Contains))) };
        if (placed.Alternatives.Count == candidates.Alternatives.Count) return selection;
        var orders = CompareOnce(request, placed, ledger, warmStart: null, clockMs, eager, prune);
        var findings = new SortedSet<string>(selection.Selection.Findings, StringComparer.Ordinal) { PlannedUnplacedReselected };
        findings.UnionWith(orders.Selection.Findings);
        return selection with
        {
            Selection = selection.Selection with
            {
                Selected = [.. selection.Selection.Selected, .. orders.Selection.Selected],
                Plan = selection.Selection.Plan! with { Allocations = [.. allocations, .. orders.Selection.Plan?.Allocations ?? []] },
                Findings = findings.ToArray(),
                Expansions = ledger.Expansions, SearchTruncated = selection.Selection.SearchTruncated || orders.Selection.SearchTruncated,
            },
            Deferred = orders.Deferred,
            OutcomeEvaluations = ledger.Evaluations, BaselineEvaluations = ledger.BaselineEvaluations, BaselineExpansions = ledger.BaselineExpansions,
            SearchPhases = EquatableArray.From(selection.SearchPhases.Concat(orders.SearchPhases.Select(p => p with { Phase = ReselectPhasePrefix + p.Phase }))),
            BoundsUnavailable = EquatableArray.From(selection.BoundsUnavailable.Concat(orders.BoundsUnavailable).Distinct().Order(StringComparer.Ordinal)),
        };
    }

    /// <summary>A selection finding: the chosen portfolio was unplaced planned sites alone, so the comparison without them adds the review's orders (#3557).</summary>
    public const string PlannedUnplacedReselected = "planned_unplaced_reselected";
    /// <summary>The phase prefix of the reselection's search phases in <see cref="SearchPhases"/>.</summary>
    public const string ReselectPhasePrefix = "reselect/";
    /// <summary>A selection finding (#3722): the comparison spent its outcome-evaluation budget; what it did not evaluate stays unseen.</summary>
    public const string OutcomeEvaluationsExhausted = "outcome_evaluations_exhausted";
    /// <summary>A selection finding (#3722): the comparison reached its traversal bound on scheduler expansions.</summary>
    public const string TraversalBoundExhausted = "traversal_bound_exhausted";

    /// <summary>The activities in <paramref name="seeds"/> and every activity whose prerequisites reach one.</summary>
    private static HashSet<string> ReachingActivities(IReadOnlyList<ForecastActivity> activities, HashSet<string> seeds)
    {
        var reaching = new HashSet<string>(seeds, StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var task in activities)
                if (!reaching.Contains(task.Id) && task.Prerequisites.Any(reaching.Contains)) grew |= reaching.Add(task.Id);
        }
        return reaching;
    }

    private static ForecastCoupledSelection CompareOnce(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, ForecastSearchLedger ledger, IReadOnlyList<string>? warmStart, Func<double>? clockMs, bool eager, bool prune)
    {
        // The in-game cost of the comparator: the review runs its planners in parallel, so only this span times it.
        using var span = Spans.StartActivity("coupled.select");
        span?.SetTag("rimgovernor.roots", candidates.Alternatives.Count + candidates.Incumbents.Count);
        span?.SetTag("rimgovernor.budget.evaluations", ledger.Budget.Evaluations);
        span?.SetTag("rimgovernor.budget.traversal", ledger.Budget.Traversal);
        request = request with { Coupled = candidates.Context, Proposed = new() };
        var baselineCommitments = request.Commitments.Concat(candidates.Incumbents).ToArray();
        var incumbentIds = candidates.Incumbents.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        // One cache for the whole search (#3468): the context's indexes and every pawn's timelines are shared across portfolios.
        var cache = new ForecastCoupled.CoupledSearchCache();
        // What this comparison had spent when this run started (#3722): the reselection run's shares and means are of what was left to it.
        var runEvaluations = ledger.DiscretionaryEvaluations; var runExpansions = ledger.DiscretionaryExpansions;
        var phaseStart = ledger.Expansions; var phaseEvaluationStart = ledger.Evaluations;
        // The baseline is mandatory: evaluated at the full per-search bound whatever the allowance, counted and reported, charged to neither.
        ledger.Mandatory = true;
        var baselineScenario = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById(baselineCommitments), cache, ledger).Coupled!;
        ledger.Mandatory = false;
        var baseline = baselineScenario.Alternatives.Count > 0 ? baselineScenario.Alternatives[0]
            : new ForecastCoupledOutcome(new([], 0, 0), default, new Dictionary<string, ForecastNeedsResult>(), []);
        // Every outcome's wellbeing is priced against this one outcome, so the deltas between outcomes do not depend on which is the baseline.
        var reference = baseline;
        var horizonTicks = request.HorizonDays * GameTime.TicksPerDay;
        // The death floor of every outcome is measured from this one reference (#3723): priced once, not per outcome.
        var referenceHarm = ForecastValue.ReferenceHarm(reference, candidates.Context, horizonTicks);
        // An outcome's consequences are staged (#3723): the floors and the cheap terms at once, the ledger when a comparison reaches
        // it or a consumer needs the complete vector. The eager test control prices the ledger here instead.
        StagedConsequences Consequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments)
        {
            var staged = StagedConsequences.Of(outcome, commitments, candidates.Context, horizonTicks, reference, referenceHarm);
            if (eager) _ = staged.Vector;
            return staged;
        }
        // Per goal, the least-broken floor over the evaluated outcomes that progress it, while no such outcome holds every floor the baseline holds (#3503).
        var goalRoutes = new Dictionary<string, (bool Open, string Floor, double Excess)>(StringComparer.Ordinal);
        void NoteGoalRoutes(StagedConsequences vector, StagedConsequences against)
        {
            var zero = vector.UrgentZero.Excess - against.UrgentZero.Excess;
            var cap = vector.BreakRisk.Excess - against.BreakRisk.Excess;
            var death = vector.Death.Excess - against.Death.Excess;
            foreach (var contribution in vector.GoalContributions)
            {
                var route = zero >= UrgentZeroResolutionTicks ? (UrgentZeroStage, zero) : death > 0 ? (DeathFloorStage, death) : cap > 0 ? (BreakRiskStage, cap) : ("", 0d);
                if (!goalRoutes.TryGetValue(contribution.Goal, out var seen) || route.Item1 == "" || !seen.Open && route.Item2 < seen.Excess)
                    goalRoutes[contribution.Goal] = (route.Item1 == "" || seen.Open, route.Item1, route.Item2);
            }
        }
        var baselineVector = Consequences(baseline, baselineCommitments);
        foreach (var candidate in baselineScenario.Alternatives.Where(a => a.Feasible == Known(true)))
        {
            var staged = Consequences(candidate, baselineCommitments);
            var rank = CompareConsequences(staged, baselineVector, out _);
            if (baseline.Feasible != Known(true) || rank < 0 || rank == 0 && candidate.Plan.LaborCost < baseline.Plan.LaborCost)
                (baseline, baselineVector) = (candidate, staged);
        }
        var best = baseline;
        var bestVector = baselineVector;
        var selected = incumbentIds.Order(StringComparer.Ordinal).ToArray();
        var bestIsBaseline = true;
        var findings = new SortedSet<string>(candidates.Findings, StringComparer.Ordinal);
        findings.UnionWith(baselineScenario.Findings);
        if (baseline.Feasible != Known(true)) findings.Add("baseline_unassessed_or_infeasible");
        var evidence = new List<ForecastPortfolioEvidence>();
        // A construction root rejected only for lacking supported service relief is not a leader, yet it is the one a service root completes.
        var unserved = new List<string[]>();
        // Establish the incumbent before spending optional search; report that mandatory work as well.
        var truncated = baselineScenario.Truncated;
        // Spend per phase and the phase that found the chosen subset (#3563).
        var phases = new List<ForecastSearchPhase>(); var phase = "baseline"; var selectedPhase = "baseline";
        // The phase's profile (#3556): wall, schedule searches and their wall, infeasible and unsupported evaluations.
        double Now() => clockMs?.Invoke() ?? 0; var phaseAt = Now(); var phaseScheduleSearches = 0; var phaseScheduleMs = 0.0; var phaseInfeasible = 0; var phaseUnsupported = 0;
        var phaseSwaps = 0; var phaseImproved = 0; var phasePruned = 0; var phaseUnbounded = 0;
        void Phase(string next)
        {
            var now = Now();
            phases.Add(new(phase, ledger.Expansions - phaseStart)
            {
                ScheduleSearches = phaseScheduleSearches, OutcomeEvaluations = ledger.Evaluations - phaseEvaluationStart, Ms = now - phaseAt,
                ScheduleMs = phaseScheduleMs, Infeasible = phaseInfeasible, Unsupported = phaseUnsupported,
                Swaps = phaseSwaps, Improved = phaseImproved, Pruned = phasePruned, Unbounded = phaseUnbounded,
            });
            (phaseStart, phaseEvaluationStart, phase, phaseAt, phaseScheduleSearches, phaseScheduleMs, phaseInfeasible, phaseUnsupported, phaseSwaps, phaseImproved, phasePruned, phaseUnbounded)
                = (ledger.Expansions, ledger.Evaluations, next, now, 0, 0.0, 0, 0, 0, 0, 0, 0);
        }
        // Need roots come in dozens of time variants per pawn; ordered by id they would spend the budget before any investment root is reached.
        var alternatives = candidates.Alternatives.Concat(candidates.Incumbents).OrderBy(c => c.ExclusiveOutcome.StartsWith("need/", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (alternatives.Select(c => c.Id).Distinct().Count() != alternatives.Length)
            throw new DomainException("duplicate portfolio candidate");
        // Roots and tasks are indexed once per search; closures, the pairwise relations and portfolios are bitsets over those
        // indexes, and a subset's keys are its bit words (#3468).
        var tasks = candidates.Context.Activities.Activities;
        var taskIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Length; i++) taskIndex.Add(tasks[i].Id, i);
        var rootIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < alternatives.Length; i++) rootIndex.Add(alternatives[i].Id, i);
        var taskWords = Bits.Words(tasks.Length); var rootWords = Bits.Words(alternatives.Length);
        // A graph-only root adds nothing when another selected root already includes all its
        // physical work. Drop that redundant subset before scheduling; keep all provider tasks
        // in the containing root's prerequisite closure. Roots with direct effects/claims stay.
        var closures = new ulong[alternatives.Length][];
        void Close(ulong[] closure, string id)
        {
            if (!taskIndex.TryGetValue(id, out var at)) throw new DomainException("unknown commitment activity: " + id);
            if (Bits.Has(closure, at)) return;
            Bits.Set(closure, at);
            foreach (var prerequisite in tasks[at].Prerequisites) Close(closure, prerequisite);
        }
        for (var i = 0; i < alternatives.Length; i++)
        {
            closures[i] = new ulong[taskWords];
            foreach (var completion in alternatives[i].ActivityCompletions) Close(closures[i], completion);
        }
        // The request's own commitments are in every evaluated portfolio (the baseline scenario has already validated their activities).
        var fixedClosure = new ulong[taskWords];
        foreach (var commitment in request.Commitments) foreach (var completion in commitment.ActivityCompletions) Close(fixedClosure, completion);
        static bool GraphOnly(ForecastCommitment c) => c.ActivityCompletions.Count > 0 && c.Outcome is null
            && c.StockChanges.Count == 0 && c.MaterialClaims.Count == 0 && c.EquipmentClaims.Count == 0
            && c.UnsupportedEffects.Count == 0 && c.MoodMemories.Count == 0
            && c.SituationalChanges.Count == 0 && c.ComfortShifts.Count == 0 && c.OutfitChanges.Count == 0;
        static bool PlannedSiteRoot(ForecastCommitment c) => c.Outcome?.Need == ForecastPlannedSite.DevelopmentNeed && GraphOnly(c with { Outcome = null });
        var unplaced = candidates.Context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        // A placed shell (#3537): the placed ring sites of a priced planned room close it as its planned ring sites do, so their
        // construction roots are the room's as a planned site root is, by ExclusiveOutcome.
        var pricedRooms = candidates.Context.PlannedRoomRoles.Keys.Where(candidates.Context.Priced).ToHashSet();
        var placedRings = candidates.Context.Closures.Where(c => pricedRooms.Contains(c.Room) && c.Placed.Count != 0)
            .Select(c => (c.Ring, Placed: c.Placed.Select(p => ConstructionOutcome + p.Site).ToArray())).ToArray();
        var placedRing = placedRings.SelectMany(c => c.Placed).ToHashSet(StringComparer.Ordinal);
        // A priced room's own alternatives (PlannedRoomCandidates: ExclusiveOutcome planned/room{R}) are valued by its credit,
        // so they are the room's as its site roots are (#3823): on a temperate start (relief 0) the gate otherwise holds the
        // first sleeping room whenever its site roots are not offered on their own.
        var pricedRoom = pricedRooms.Select(r => FormattableString.Invariant($"planned/room{r}")).ToHashSet(StringComparer.Ordinal);
        // Per root: whether it is graph-only, keys a schedule by id (direct effects or claims; a retained incumbent) and exempts
        // its closure from the relief gate: a planned site root (#3651), a priced room's placed ring site (#3537) or alternative
        // (#3823), a root that requests relief, or one that dresses a pawn (a garment priced by the thoughts and armor it changes, #3816).
        var graphOnly = new bool[alternatives.Length]; var keyed = new bool[alternatives.Length]; var exempting = new bool[alternatives.Length];
        for (var i = 0; i < alternatives.Length; i++)
        {
            graphOnly[i] = GraphOnly(alternatives[i]);
            var plannedSite = PlannedSiteRoot(alternatives[i]);
            keyed[i] = !graphOnly[i] && !plannedSite || incumbentIds.Contains(alternatives[i].Id);
            exempting[i] = plannedSite || alternatives[i].Outcome?.Requirements is { Relief.Count: > 0 } || alternatives[i].OutfitChanges.Count > 0
                || alternatives[i].ExclusiveOutcome is { } exclusive && (placedRing.Contains(exclusive) || pricedRoom.Contains(exclusive));
        }
        // A planned room closes only once every ring site of one stuff is built, so one site root alone carries its labor and
        // material and none of the room's value (a bedroom's credit, #3655): adding roots one at a time never reaches the
        // pair. Each room alternative's site roots (the first variant per site, by id) are also tried as one unit (#3647).
        var units = new List<int[]>();
        foreach (var room in candidates.Context.PlannedRooms)
        {
            var members = room.Sites.Select(site => Array.FindIndex(alternatives, c => c.ExclusiveOutcome == site && PlannedSiteRoot(c))).ToArray();
            if (members.Length > 1 && members.All(m => m >= 0) && !units.Any(u => u.SequenceEqual(members))) units.Add(members);
        }
        // A placed shell's room closes only once all its ring sites, placed and planned, are finished (#3537): one unit too.
        foreach (var (ring, placed) in placedRings)
        {
            var members = ring.Select(site => Array.FindIndex(alternatives, c => c.ExclusiveOutcome == site && PlannedSiteRoot(c)))
                .Concat(placed.Select(site => Array.FindIndex(alternatives, c => c.ExclusiveOutcome == site))).ToArray();
            if (members.Length > 1 && members.All(m => m >= 0) && !units.Any(u => u.SequenceEqual(members))) units.Add(members);
        }
        // Per root, the roots redundant with it (a graph-only one's closure inside the other's) and the roots it cannot share a portfolio with.
        var redundant = new ulong[alternatives.Length][]; var incompatible = new ulong[alternatives.Length][];
        for (var i = 0; i < alternatives.Length; i++) { redundant[i] = new ulong[rootWords]; incompatible[i] = new ulong[rootWords]; }
        for (var i = 0; i < alternatives.Length; i++)
            for (var j = i + 1; j < alternatives.Length; j++)
            {
                if (graphOnly[i] && Bits.SubsetOf(closures[i], closures[j]) || graphOnly[j] && Bits.SubsetOf(closures[j], closures[i]))
                { Bits.Set(redundant[i], j); Bits.Set(redundant[j], i); }
                if (!Compatible(alternatives[i], alternatives[j])) { Bits.Set(incompatible[i], j); Bits.Set(incompatible[j], i); }
            }
        // The request's own commitments are in every evaluated portfolio.
        var fixedCompatible = true;
        for (var i = 0; i < request.Commitments.Count; i++)
            for (var j = i + 1; j < request.Commitments.Count; j++) fixedCompatible &= Compatible(request.Commitments[i], request.Commitments[j]);
        var fixedIncompatible = new bool[alternatives.Length];
        for (var i = 0; i < alternatives.Length; i++)
            foreach (var commitment in request.Commitments) fixedIncompatible[i] |= !Compatible(commitment, alternatives[i]);
        bool CompatibleSet(ReadOnlySpan<ulong> members)
        {
            for (var i = 0; i < alternatives.Length; i++)
                if (Bits.Has(members, i) && !Bits.Disjoint(incompatible[i], members)) return false;
            return true;
        }
        ForecastCommitment[] Members(ReadOnlySpan<ulong> members)
        {
            var rows = new List<ForecastCommitment>();
            for (var i = 0; i < alternatives.Length; i++) if (Bits.Has(members, i)) rows.Add(alternatives[i]);
            return rows.ToArray();
        }
        // The urgent-zero unserved-pawn bound (#3724), the one bound proven sound under the comparator; the others are unavailable.
        //
        // Claim. Let S be a portfolio (the request's commitments plus a subset of roots) and p a pawn none of whose need tasks
        // (context.NeedTasks rows with Pawn p) lies in the task closure of S. Then in every plan of S, p's food and rest zero
        // times equal the baseline's, and the plan's urgent-zero excess is at least max(p's food zero time, p's rest zero time).
        // Proof. A plan allocates exactly the tasks in the closure, so none of p's need tasks is allocated. The evaluator builds
        // p's need choices only from p's own allocated need tasks (CoupledSearchCache.Evaluate), so p's choices are empty in every
        // plan of S, as in the baseline, whose closure is a subset of S's. The needs core (ForecastNeeds.NeedsCoreOf) derives the
        // food and rest timelines from the pawn's observed level, fall and threshold and its own choices alone; the grants, renewals
        // and occupancy a plan adds reach only the mood and the capacity, never ZeroTicks. The urgent-zero time of an outcome is the
        // maximum over all pawns of max(food, rest) ZeroTicks (ConsequenceTerms.Urgent[0]) and its excess is that time floored at 0,
        // so it is at least p's. The comparator decides at the urgent-zero stage whenever the excesses differ by at least
        // UrgentZeroResolutionTicks (CompareConsequences), the worse excess losing; so when some unserved pawn's baseline zero time
        // exceeds the leader's excess by that much, every feasible plan of S loses to the leader of this moment at that stage, and an
        // infeasible plan is never ranked. S is skipped and cached at the bound it was skipped at, like an evaluated subset whose
        // schedule stopped there: the leader evolves as it would have, and the confirm and the sweep meet S again at their higher
        // bound, where it is skipped again or, if the leader's excess has since grown (ties within the tick resolution can move it by
        // less than a tick), evaluated. A skipped subset counts in the sweep's mean cost per subset, so the pruned search affords
        // the sweep whenever the unpruned one does.
        // Domain. The claim needs every plan of S to carry an assessed urgent-zero floor, since an unassessed floor reads as excess 0
        // and would pass: food is assessed iff the pawn's mechanics are observed (plan-independent), and rest likewise while no plan
        // sleeps the pawn (SleepServiceEnds reads the sleep choices' times and nutrition, so a rest service in the closure can leave rest
        // unassessed in some plans), hence the bound is unavailable when the closure holds a rest need task, or when any pawn's
        // baseline zero time is unknown. It also needs a feasible leader with an assessed floor, and a context without goal values,
        // since every evaluated outcome reports its goal routes (NoteGoalRoutes) and a skipped one would not. A single root whose
        // closure holds construction is evaluated while the relief gate still collects unserved roots, so that the extension order
        // the gate feeds (unserved) stays the same. Served pawns are never bounded: their zero time can only improve with the plan.
        // No sound bound exists before evaluation for the death and break-risk floors (both read the plan's mood and harm), the ledger
        // (gain terms, the shared-assessed-term comparison and its tolerance) or the tiebreaks; those subsets are evaluated within the
        // #3722 allowance and the phase counts them as Unbounded with the reason in BoundsUnavailable.
        // #3720 plugs in here: a finalist shortlist compares the unserved pawns' zero time with the weakest retained finalist's
        // urgent-zero excess in place of the leader's (bestVector.UrgentZero.Excess below), so a subset that cannot enter the
        // shortlist is skipped and one that could is evaluated; the claim and domain are unchanged.
        var pawnRows = candidates.Context.Needs.Pawns;
        var pawnWords = Bits.Words(pawnRows.Count);
        var pawnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p < pawnRows.Count; p++) pawnIndex.TryAdd(pawnRows[p].Pawn, p);
        // Per task: the pawns whose need it serves, whether it is a rest service, and whether the relief gate reads it as construction.
        var taskServes = new ulong[tasks.Length][]; var taskRest = new bool[tasks.Length]; var taskConstruction = new bool[tasks.Length];
        foreach (var need in candidates.Context.NeedTasks)
        {
            if (!taskIndex.TryGetValue(need.Task, out var at) || !pawnIndex.TryGetValue(need.Pawn, out var p)) continue;
            Bits.Set(taskServes[at] ??= new ulong[pawnWords], p);
            taskRest[at] |= need.Kind == "rest";
        }
        foreach (var id in unplaced) if (taskIndex.TryGetValue(id, out var at)) taskConstruction[at] = true;
        foreach (var (binding, task) in candidates.Context.Bindings)
            if ((task.SiteDefinition.Length != 0 || task.Job == "BuildRoof") && taskIndex.TryGetValue(binding.Task, out var at)) taskConstruction[at] = true;
        void Summarize(ReadOnlySpan<ulong> closure, Span<ulong> served, ref bool rest, ref bool construction)
        {
            for (var t = 0; t < tasks.Length; t++)
            {
                if (!Bits.Has(closure, t)) continue;
                if (taskServes[t] is { } pawns) Bits.Or(served, pawns);
                rest |= taskRest[t]; construction |= taskConstruction[t];
            }
        }
        var servedBy = new ulong[alternatives.Length][]; var restIn = new bool[alternatives.Length]; var constructionIn = new bool[alternatives.Length];
        for (var i = 0; i < alternatives.Length; i++)
        { servedBy[i] = new ulong[pawnWords]; Summarize(closures[i], servedBy[i], ref restIn[i], ref constructionIn[i]); }
        var fixedServed = new ulong[pawnWords]; var fixedRest = false; var fixedConstruction = false;
        Summarize(fixedClosure, fixedServed, ref fixedRest, ref fixedConstruction);
        var servedScratch = new ulong[pawnWords];
        // Each pawn's zero time in the baseline, the larger of food and rest; the bound is unavailable while any is unknown.
        var baselineZero = new double[pawnRows.Count]; var baselineZeroKnown = pawnRows.Count > 0;
        for (var p = 0; p < pawnRows.Count && baselineZeroKnown; p++)
        {
            if (!baseline.Needs.TryGetValue(pawnRows[p].Pawn, out var row)) { baselineZeroKnown = false; break; }
            foreach (var metric in row.Needs)
            {
                if (metric.Kind is not ("food" or "rest")) continue;
                if (!metric.ZeroTicks.TryGetValue(out var zero)) { baselineZeroKnown = false; break; }
                baselineZero[p] = Math.Max(baselineZero[p], zero);
            }
        }
        var boundsUnavailable = new SortedSet<string>(StringComparer.Ordinal);
        // Why the bound cannot be formed for a portfolio whose closure's rest services are as given, against the leader of this moment; null when it can.
        string? UrgentZeroBoundUnavailable(bool restInClosure)
        {
            if (candidates.Context.GoalValues.Count > 0) return "goal_routes";
            if (!baselineZeroKnown) return "baseline_zero_unassessed";
            if (best.Feasible != Known(true) || !bestVector.UrgentZero.Assessed) return "leader_unassessed";
            return restInClosure ? "rest_service_in_closure" : null;
        }
        // The pawn whose baseline zero time proves every plan serving only the pawns in `served` loses at the urgent-zero stage; null when none does.
        string? UrgentZeroLoser(ReadOnlySpan<ulong> served)
        {
            var worst = -1;
            for (var p = 0; p < pawnRows.Count; p++)
                if (!Bits.Has(served, p) && (worst < 0 || baselineZero[p] > baselineZero[worst])) worst = p;
            return worst >= 0 && baselineZero[worst] - bestVector.UrgentZero.Excess >= UrgentZeroResolutionTicks ? pawnRows[worst].Pawn : null;
        }
        // The traversal reaches a subset by more than one phase, and different root sets schedule the same work (a
        // shelter, a room alternative or the room's site roots completing the same sites). Subsets whose task closure
        // and non-graph roots (direct effects or claims; a retained incumbent by id) are equal share one schedule
        // (#3334). A planned site root's only non-graph effect is exempting its closure from the relief gate (#3651), which
        // the gate below reads per subset; an equal exempt closure too is the same evaluation, skipped. A
        // schedule search that finished within its bound is complete at any larger bound; a truncated one is redone
        // at a larger bound.
        // A subset's schedule key is its closure union over the tasks then its keyed roots; its evaluation key adds the
        // closure union of its exempting roots. The words are built in one scratch and copied only when first seen.
        var scheduleWords = taskWords + rootWords; var keyWords = scheduleWords + taskWords;
        var keyScratch = new ulong[keyWords]; var memberScratch = new ulong[rootWords];
        var evaluatedByKey = new Dictionary<ulong[], int>(WordsComparer.Instance);
        var evaluated = evaluatedByKey.GetAlternateLookup<ReadOnlySpan<ulong>>();
        var schedulesByKey = new Dictionary<ulong[], (int Bound, ForecastCoupledScenario Scenario)>(WordsComparer.Instance);
        var schedules = schedulesByKey.GetAlternateLookup<ReadOnlySpan<ulong>>();
        // A screened schedule's truncation and findings stand only while no full-bound schedule of the subset follows.
        var screenedByKey = new Dictionary<ulong[], (string[] Findings, bool Truncated)>(WordsComparer.Instance);
        var screened = screenedByKey.GetAlternateLookup<ReadOnlySpan<ulong>>();
        bool Evaluate(ForecastCommitment[] additions, int bound = PortfolioBound)
        {
            Array.Clear(keyScratch); Array.Clear(memberScratch);
            foreach (var c in additions)
            {
                var at = rootIndex[c.Id];
                Bits.Set(memberScratch, at);
                Bits.Or(keyScratch.AsSpan(0, taskWords), closures[at]);
                if (keyed[at]) Bits.Set(keyScratch.AsSpan(taskWords, rootWords), at);
                if (exempting[at]) Bits.Or(keyScratch.AsSpan(scheduleWords, taskWords), closures[at]);
            }
            ReadOnlySpan<ulong> key = keyScratch, schedule = keyScratch.AsSpan(0, scheduleWords);
            if (evaluated.TryGetValue(key, out var done) && done >= bound) return true;
            var commitments = request.Commitments.Concat(additions).ToArray();
            var compatible = fixedCompatible && CompatibleSet(memberScratch);
            foreach (var c in additions) compatible &= !fixedIncompatible[rootIndex[c.Id]];
            if (!compatible) return true;
            // A spent allowance starts nothing more, a cached schedule included (#3722), and skips nothing on a bound either, so the
            // pruned search ends where the unpruned one does.
            if (ledger.Exhausted) { truncated = true; return false; }
            if (prune)
            {
                // The bound (#3724, above): the pawns the portfolio's closure serves, then the pawn none of its plans can keep from starving.
                Array.Clear(servedScratch); Bits.Or(servedScratch, fixedServed);
                var restInClosure = fixedRest; var constructionInClosure = fixedConstruction;
                foreach (var c in additions)
                { var at = rootIndex[c.Id]; Bits.Or(servedScratch, servedBy[at]); restInClosure |= restIn[at]; constructionInClosure |= constructionIn[at]; }
                var unavailable = UrgentZeroBoundUnavailable(restInClosure);
                if (unavailable is null && additions.Length == 1 && unserved.Count < 4 && constructionInClosure) unavailable = "relief_gate_order";
                if (unavailable is not null) { boundsUnavailable.Add(UrgentZeroUnservedBound + ":" + unavailable); phaseUnbounded++; }
                else if (UrgentZeroLoser(servedScratch) is { } starving)
                {
                    evaluated[key] = bound;
                    phasePruned++;
                    evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), null, ForecastPortfolioEvidence.PrunedPrefix + UrgentZeroUnservedBound + ":" + starving));
                    return true;
                }
            }
            evaluated[key] = bound;
            if (!schedules.TryGetValue(schedule, out var known) || known.Bound < bound)
            {
                var searchAt = Now();
                var searched = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById(commitments), cache, ledger, bound).Coupled!;
                phaseScheduleMs += Now() - searchAt; phaseScheduleSearches++;
                known = (searched.Truncated ? bound : PortfolioBound, searched);
                schedules[schedule] = known;
                if (known.Bound < PortfolioBound) screened[schedule] = (searched.Findings.ToArray(), searched.Truncated);
                else
                {
                    screened.Remove(schedule);
                    findings.UnionWith(searched.Findings);
                    if (searched.Truncated) truncated = true;
                }
            }
            evaluated[key] = known.Bound;
            var scenario = known.Scenario;
            var feasible = scenario.Alternatives.Where(a => a.Feasible == Known(true)).ToArray();
            if (feasible.Length == 0)
            {
                phaseInfeasible++;
                evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), null, ForecastPortfolioEvidence.InfeasibleOrUnassessed));
                foreach (var reason in scenario.Alternatives.SelectMany(a => a.Findings)) findings.Add(reason.Reason + ":" + reason.Commitment);
            }
            foreach (var outcome in feasible)
            {
                var vector = Consequences(outcome, commitments);
                NoteGoalRoutes(vector, baselineVector);
                // Physical feasibility does not certify optional investment. Partial shells retain
                // unknown exposure; they cannot win by hiding it. Keep expanding complementary roots.
                // Construction in the closure of a selected exempting root (a planned site root, #3651; a priced room's placed ring
                // site, #3537, or alternative, #3823; a root with its own relief) is exempt, and so is a planned room's roof cell
                // when the schedule finishes that room's roof.
                var roofed = candidates.Context.Shelters.Where(r => r.PlannedRoom != 0 && r.RoofCells.Count != 0
                        && outcome.RequestedDevelopment.Contains(ForecastPlannedSite.RoofUnit(r.PlannedRoom)))
                    .SelectMany(r => r.RoofCells.SelectMany(cell => cell)).ToHashSet(StringComparer.Ordinal);
                var unsupported = false;
                foreach (var a in outcome.Plan.Allocations)
                {
                    if (taskIndex.TryGetValue(a.Activity, out var at) && Bits.Has(keyScratch.AsSpan(scheduleWords, taskWords), at) || roofed.Contains(a.Activity)) continue;
                    if (unplaced.Contains(a.Activity) || candidates.Context.Bindings.TryGetValue((a.Activity, a.Pawn), out var binding)
                        && (binding.SiteDefinition.Length != 0 || binding.Job == "BuildRoof")) { unsupported = true; break; }
                }
                if (unsupported && (!outcome.StationaryThermalRelief.IsKnown || outcome.StationaryThermalRelief.Value <= 0))
                {
                    phaseUnsupported++;
                    if (unserved.Count < 4 && additions.Length == 1) unserved.Add([additions[0].Id]);
                    evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector.VectorIfPriced, "construction_without_supported_service_relief" + string.Concat(outcome.Findings.Where(f => f.Reason.StartsWith("thermal_relief_unassessed", StringComparison.Ordinal)).Select(f => ":" + f.Reason))
                        + (outcome.StationaryThermalRelief.IsKnown ? ":relief=" + outcome.StationaryThermalRelief.Value : "")));
                    continue;
                }
                var rank = CompareConsequences(vector, bestVector, out var stage);
                // Consequence ties never release a retained incumbent to save nominal labor.
                var retainsSelectedIncumbents = selected.Where(incumbentIds.Contains).All(id => additions.Any(c => c.Id == id));
                if (best.Feasible != Known(true) || rank < 0 || rank == 0 && !bestIsBaseline && retainsSelectedIncumbents && vector.Labor < bestVector.Labor)
                {
                    var ids = additions.Select(c => c.Id).ToArray();
                    if (!ids.SequenceEqual(selected, StringComparer.Ordinal)) { selectedPhase = phase; phaseImproved++; }
                    best = outcome; bestVector = vector; selected = ids; bestIsBaseline = false;
                }
                else
                {
                    // A tie leaves the caller's rule to decide: the incumbent (the baseline, or a retained incumbent the subset drops) or labor.
                    if (rank == 0) stage = bestIsBaseline || !retainsSelectedIncumbents ? IncumbentStage : LaborStage;
                    evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector.VectorIfPriced, ForecastPortfolioEvidence.LostToLeaderPrefix + stage));
                }
            }
            return !ledger.Exhausted;
        }
        // Breadth by cardinality prevents one deep branch from hiding every independent source. A subset's present value never
        // stands for its completions (two individually useless contributions can remain complementary): the only pruning is the
        // proven bound above, applied per subset and, in the sweep, to a prefix whose every completion it covers.
        // The seed pass may stop starting evaluations once it has spent its share of either allowance left to this run; that is a
        // hand-off, not truncation.
        var seedEvaluationLimit = int.MaxValue; var seedExpansionLimit = int.MaxValue; var seedSpent = false; var leafBound = PortfolioBound;
        var partialBits = new ulong[rootWords];
        bool Search(int start, int left, List<ForecastCommitment> partial)
        {
            if (left == 0)
            {
                if (ledger.DiscretionaryEvaluations >= seedEvaluationLimit || ledger.DiscretionaryExpansions >= seedExpansionLimit) { seedSpent = true; return false; }
                return Evaluate(partial.ToArray(), leafBound);
            }
            if (prune && partial.Count > 0 && start <= alternatives.Length - left)
            {
                // Every completion of this prefix adds roots from `start` on, so a pawn served by neither the prefix nor any of those
                // starves as in the baseline in every leaf below (#3724): the whole branch is skipped when that alone loses to the leader.
                Array.Clear(servedScratch); Bits.Or(servedScratch, fixedServed);
                var restInClosure = fixedRest;
                foreach (var c in partial) { var at = rootIndex[c.Id]; Bits.Or(servedScratch, servedBy[at]); restInClosure |= restIn[at]; }
                for (var j = start; j < alternatives.Length; j++) { Bits.Or(servedScratch, servedBy[j]); restInClosure |= restIn[j]; }
                if (UrgentZeroBoundUnavailable(restInClosure) is null && UrgentZeroLoser(servedScratch) is { } starving)
                {
                    phasePruned++;
                    evidence.Add(new(EquatableArray.From(partial.Select(c => c.Id)), null, ForecastPortfolioEvidence.PrunedBranchPrefix + UrgentZeroUnservedBound + ":" + starving));
                    return true;
                }
            }
            for (var i = start; i <= alternatives.Length - left; i++)
            {
                if (!Bits.Disjoint(incompatible[i], partialBits) || !Bits.Disjoint(redundant[i], partialBits)) continue;
                partial.Add(alternatives[i]); Bits.Set(partialBits, i);
                var more = Search(i + 1, left - 1, partial);
                partial.RemoveAt(partial.Count - 1); Bits.Clear(partialBits, i);
                if (!more) return false;
            }
            return true;
        }
        // Extend the best portfolio found so far with each other root, in root order, before the
        // pairwise sweep: when single roots exhaust the budget, the only way to reach a second
        // contribution is to add it to the leader rather than to enumerate every pair.
        var portfolioBits = new ulong[rootWords];
        bool Extend(string[] leader)
        {
            var seed = new ulong[rootWords];
            foreach (var id in leader) Bits.Set(seed, rootIndex[id]);
            var unservedRoot = new bool[alternatives.Length];
            foreach (var u in unserved) unservedRoot[rootIndex[u[0]]] = true;
            // Roots are ordered by id, so the unserved construction a leader would complete sits behind hundreds of service roots and the budget ends first.
            // A root exclusive with a member swaps it out (another rest choice for the leader's), after every addition.
            var order = Enumerable.Range(0, alternatives.Length).Where(i => !Bits.Has(seed, i))
                .OrderBy(i => unservedRoot[i] ? 0 : 1).ThenBy(i => Bits.Disjoint(incompatible[i], seed) ? 0 : 1).ToArray();
            foreach (var root in order)
            {
                for (var w = 0; w < rootWords; w++) portfolioBits[w] = seed[w] & ~incompatible[root][w];
                Bits.Set(portfolioBits, root);
                if (!CompatibleSet(portfolioBits) || !Bits.Disjoint(redundant[root], portfolioBits)) continue;
                if (!Bits.Disjoint(incompatible[root], seed)) phaseSwaps++;
                if (!Evaluate(Members(portfolioBits), ScreenBound)) return false;
            }
            foreach (var unit in units)
            {
                if (unit.All(i => Bits.Has(seed, i))) continue;
                Array.Copy(seed, portfolioBits, rootWords);
                foreach (var i in unit) for (var w = 0; w < rootWords; w++) portfolioBits[w] &= ~incompatible[i][w];
                foreach (var i in unit) Bits.Set(portfolioBits, i);
                if (!CompatibleSet(portfolioBits) || unit.Any(i => !Bits.Disjoint(redundant[i], portfolioBits))) continue;
                if (!Evaluate(Members(portfolioBits), ScreenBound)) return false;
            }
            return true;
        }
        var continueSearch = candidates.Incumbents.Count == 0 || Evaluate([]);
        // The previous review's selection first (#3563): each id by exact match, else the first variant of the same root before
        // "/composition/" (the composition numbers a root's variants per review), screened like a seed. When it still beats
        // the baseline the greedy extension starts from it instead of re-finding it one root at a time.
        var warmStartState = "";
        if (warmStart is { Count: > 0 })
        {
            var byPrefix = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = alternatives.Length - 1; i >= 0; i--) byPrefix[RootPrefix(alternatives[i].Id)] = i;
            var warm = new SortedSet<int>();
            foreach (var id in warmStart)
                if (rootIndex.TryGetValue(id, out var exact)) warm.Add(exact);
                else if (byPrefix.TryGetValue(RootPrefix(id), out var variant)) warm.Add(variant);
            warmStartState = warm.Count == 0 ? "missing" : "seeded";
            if (warm.Count > 0 && continueSearch) { Phase("warm"); continueSearch = Evaluate(warm.Select(i => alternatives[i]).ToArray(), ScreenBound); }
        }
        if (continueSearch)
        {
            Phase("seed");
            // Seed pass: screened single roots until a quarter of the allowance left to this run is spent (every root if that is
            // cheaper), at least one unit of each so that a tiny allowance still screens a root.
            seedEvaluationLimit = runEvaluations + Math.Max(1, (ledger.Budget.Evaluations - runEvaluations) / SeedShare);
            seedExpansionLimit = runExpansions + Math.Max(1, (ledger.Budget.Traversal - runExpansions) / SeedShare);
            leafBound = ScreenBound;
            continueSearch = Search(0, 1, []) || seedSpent;
            seedEvaluationLimit = int.MaxValue; seedExpansionLimit = int.MaxValue; leafBound = PortfolioBound;
            for (var u = 0; continueSearch && u < units.Count; u++)
                continueSearch = Evaluate(units[u].Select(i => alternatives[i]).ToArray(), ScreenBound);
            if (!continueSearch) truncated = true;
        }
        // Pair each unserved construction root with the other roots (service, rest, food) so the investment is judged with its use.
        if (continueSearch && bestIsBaseline)
        {
            Phase("unserved");
            foreach (var leader in unserved.ToArray())
                if (!Extend(leader)) { continueSearch = false; truncated = true; break; }
        }
        if (continueSearch && !bestIsBaseline)
        {
            Phase("extend");
            // Greedy to a fixpoint: a portfolio that beat the leader becomes the leader and is extended in turn, so
            // a set of any cardinality is reached one root at a time. Each level is bounded by the roots, not by a
            // sweep of subsets, and ends when no root improves the leader. Selection is ranked by the same
            // consequence order as every other evaluation; the final leader is then confirmed at the full bound.
            string[] before;
            do
            {
                before = selected;
                if (!Extend(before)) { continueSearch = false; truncated = true; }
            }
            while (continueSearch && !selected.SequenceEqual(before, StringComparer.Ordinal));
            if (continueSearch)
            {
                Phase("confirm");
                var members = selected.ToHashSet(StringComparer.Ordinal);
                if (!Evaluate(alternatives.Where(c => members.Contains(c.Id)).ToArray())) { continueSearch = false; truncated = true; }
            }
        }
        // The cardinality sweep is complete only when it finishes: a sweep the budget cuts short has evaluated the subsets
        // in root order up to some cardinality, a lottery over the root order rather than a search (in game, 0 of 43
        // selections came from it while it spent 13,000 of the 16,384 expansions per review, #3563). So it runs only when
        // the allowance left can finish it: the subsets not yet evaluated at this run's mean cost per evaluated subset, in outcome
        // evaluations and in expansions (#3722). Otherwise the search ends converged at the confirmed leader, truncated as before
        // (the sweep was never finishing either).
        if (continueSearch)
        {
            var subsetsSeen = Math.Max(1, evaluatedByKey.Count);
            var affordable = Math.Min(ledger.EvaluationsLeft / Math.Max(1, (ledger.DiscretionaryEvaluations - runEvaluations) / subsetsSeen),
                ledger.TraversalLeft / Math.Max(1, (ledger.DiscretionaryExpansions - runExpansions) / subsetsSeen));
            var subsets = alternatives.Length >= 62 ? long.MaxValue : (1L << alternatives.Length) - 1 - subsetsSeen;
            if (subsets > affordable) { continueSearch = false; truncated = true; }
        }
        if (continueSearch) Phase("sweep");
        for (var size = 1; continueSearch && size <= alternatives.Length; size++)
            if (!Search(0, size, [])) { truncated = true; break; }
        Phase("end");
        foreach (var (reasons, cut) in screenedByKey.Values) { findings.UnionWith(reasons); truncated |= cut; }
        if (ledger.EvaluationsExhausted) findings.Add(OutcomeEvaluationsExhausted);
        if (ledger.TraversalExhausted) findings.Add(TraversalBoundExhausted);
        if (truncated) findings.Add("portfolio_search_truncated");
        if (candidates.Truncated) findings.Add("portfolio_generation_truncated");
        foreach (var (goal, route) in goalRoutes)
            if (!route.Open) findings.Add(GoalInfeasiblePrefix + goal + ":" + route.Floor);
        findings.Add("beyond_horizon_unassessed");
        // The selected and baseline vectors are public and complete (#3723): priced here when no comparison reached their ledgers.
        var selectedVector = bestVector.Vector;
        // The harm term and the death floor are reported unassessed, with the specific finding, when the chosen outcome could not price
        // them (#3546); a break risk no mood threshold supports leaves that floor unchecked.
        if (selectedVector.Value.Ledger.Harm is LedgerTerm.Unassessed)
        {
            findings.Add("harm_capability_unassessed");
            findings.UnionWith(selectedVector.Value.Ledger.HarmFindings);
        }
        if (!selectedVector.Value.Death.Assessed) findings.Add("death_floor_unassessed");
        if (!selectedVector.Value.BreakRisk.Assessed) findings.Add("break_floor_unassessed");
        var deferred = DeferUnselectedPawns(candidates.Context, best.Plan);
        span?.SetTag("rimgovernor.expansions", ledger.Expansions);
        span?.SetTag("rimgovernor.evaluations", ledger.Evaluations);
        span?.SetTag("rimgovernor.schedules", schedulesByKey.Count);
        span?.SetTag("rimgovernor.truncated", truncated);
        return new(new(selected, best.Plan, findings.ToArray(), ledger.Expansions, truncated), baseline, best,
            baselineVector.Vector, selectedVector, EquatableArray.From(evidence), request.HorizonDays * GameTime.TicksPerDay, candidates.Truncated)
        { CompositionExpansions = candidates.CompositionExpansions,
            ServiceInvestments = ForecastServiceInvestments.ServiceInvestmentsOf(baseline, best, candidates.Context, request.HorizonDays * GameTime.TicksPerDay),
            Deferred = deferred,
            SearchPhases = EquatableArray.From(phases), SelectedPhase = selectedPhase, WarmStart = warmStartState,
            Budget = ledger.Budget, OutcomeEvaluations = ledger.Evaluations, BaselineEvaluations = ledger.BaselineEvaluations, BaselineExpansions = ledger.BaselineExpansions,
            BoundsUnavailable = EquatableArray.From(boundsUnavailable) };
    }

    /// <summary>The urgent-zero unserved-pawn bound (#3724), as pruning evidence and <see cref="BoundsUnavailable"/> name it: a pawn none of
    /// a portfolio's need tasks serve starves as in the baseline in every plan of it, so the portfolio loses at the urgent-zero stage when
    /// that alone exceeds the leader's excess by <see cref="UrgentZeroResolutionTicks"/>. The proof and domain are at the bound in <c>CompareOnce</c>.</summary>
    internal const string UrgentZeroUnservedBound = "urgent_zero_unserved";

    /// <summary>A root's id before its per-review composition number (#3563).</summary>
    private static string RootPrefix(string id)
    {
        var at = id.IndexOf("/composition/", StringComparison.Ordinal);
        return at < 0 ? id : id[..at];
    }

    /// <summary>Whether two distinct commitments can share a portfolio: no common exclusive outcome, and neither's outcome
    /// excludes the other or claims the other's exclusive output.</summary>
    private static bool Compatible(ForecastCommitment a, ForecastCommitment b)
        => (a.ExclusiveOutcome.Length == 0 || a.ExclusiveOutcome != b.ExclusiveOutcome) && (a.Id == b.Id || !Excludes(a, b) && !Excludes(b, a));

    private static bool Excludes(ForecastCommitment c, ForecastCommitment other) => c.Outcome is { } effect
        && (effect.Excludes.Contains(other.Outcome?.Id ?? other.Id) || effect.ExclusiveOutput.Length != 0 && effect.ExclusiveOutput == other.Outcome?.ExclusiveOutput);

    /// <summary>Bit sets over an index, as 64-bit words (#3468).</summary>
    private static class Bits
    {
        public static int Words(int count) => (count + 63) >> 6;
        public static bool Has(ReadOnlySpan<ulong> words, int at) => (words[at >> 6] & 1UL << (at & 63)) != 0;
        public static void Set(Span<ulong> words, int at) => words[at >> 6] |= 1UL << (at & 63);
        public static void Clear(Span<ulong> words, int at) => words[at >> 6] &= ~(1UL << (at & 63));
        public static void Or(Span<ulong> words, ReadOnlySpan<ulong> other) { for (var w = 0; w < words.Length; w++) words[w] |= other[w]; }
        public static bool Disjoint(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
        {
            for (var w = 0; w < a.Length; w++) if ((a[w] & b[w]) != 0) return false;
            return true;
        }
        public static bool SubsetOf(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
        {
            for (var w = 0; w < a.Length; w++) if ((a[w] & ~b[w]) != 0) return false;
            return true;
        }
    }

    /// <summary>Bit words compared by value and looked up by span, so a key is copied only when first seen (#3468).</summary>
    private sealed class WordsComparer : IEqualityComparer<ulong[]>, IAlternateEqualityComparer<ReadOnlySpan<ulong>, ulong[]>
    {
        internal static readonly WordsComparer Instance = new();
        public bool Equals(ulong[]? x, ulong[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(ulong[] words) => GetHashCode((ReadOnlySpan<ulong>)words);
        public bool Equals(ReadOnlySpan<ulong> alternate, ulong[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<ulong> alternate)
        {
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(alternate));
            return hash.ToHashCode();
        }
        public ulong[] Create(ReadOnlySpan<ulong> alternate) => alternate.ToArray();
    }

    public static ForecastConsequenceVector CoupledConsequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments)
        => CoupledConsequences(outcome, commitments, null, 0, null);

    private enum Term { ZeroTicks, ThresholdDepthTicks, ThresholdExposureTicks, ZeroAtHorizon }

    /// <summary>One need term over the outcome's pawns, in order, as Enumerable.Sum adds and Enumerable.Max (a NaN gives way to
    /// the next value) compares; unknown when any pawn's is, each such pawn noted in <paramref name="unknown"/>.</summary>
    private static Fact<double> Metric(Dictionary<string, ForecastNeedsResult> rows, List<string> unknown, string kind, Term term, bool maximum = false)
    {
        double sum = 0, max = 0;
        var any = false;
        var complete = rows.Count > 0;
        foreach (var (pawn, needs) in rows)
            foreach (var metric in needs.Needs)
            {
                if (metric.Kind != kind) continue;
                var value = term switch
                {
                    Term.ZeroTicks => metric.ZeroTicks,
                    Term.ThresholdDepthTicks => metric.ThresholdDepthTicks,
                    Term.ThresholdExposureTicks => metric.ThresholdExposureTicks,
                    _ => metric.FinalLevel.IsKnown ? Known(metric.FinalLevel.Value == 0 ? 1.0 : 0.0) : default
                };
                if (!value.TryGetValue(out var number)) { unknown.Add("unassessed:" + pawn + ":" + kind); complete = false; continue; }
                sum += number;
                if (!any || double.IsNaN(max) || number > max) max = number;
                any = true;
            }
        return complete ? Known(maximum ? max : sum) : default;
    }

    private static Fact<double> Worst(Fact<double> a, Fact<double> b) => a.IsKnown && b.IsKnown ? Known(Math.Max(a.Value, b.Value)) : default;

    /// <summary>The complete vector, priced by the ledger against <paramref name="reference"/> when a <paramref name="context"/> is given (#3522).</summary>
    internal static ForecastConsequenceVector CoupledConsequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments,
        ForecastCoupledContext? context, double horizonTicks, ForecastCoupledOutcome? reference)
        => context is null ? ConsequenceTerms(outcome, commitments, null)
        : StagedConsequences.Of(outcome, commitments, context, horizonTicks, reference!, ForecastValue.ReferenceHarm(reference!, context, horizonTicks)).Vector;

    /// <summary>The terms of the vector read from the outcome alone (urgency, needs, mood, development, labor, support, maintenance,
    /// unknowns), with an empty <see cref="ForecastConsequenceVector.Value"/>: what a staged comparison has before the ledger (#3723).</summary>
    private static ForecastConsequenceVector ConsequenceTerms(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments,
        ForecastCoupledContext? context)
    {
        var rows = outcome.Needs as Dictionary<string, ForecastNeedsResult> ?? new Dictionary<string, ForecastNeedsResult>(outcome.Needs, StringComparer.Ordinal);
        var unknown = new List<string>();
        var urgent = new Fact<double>[]
        {
            Worst(Metric(rows, unknown, "food", Term.ZeroTicks, true), Metric(rows, unknown, "rest", Term.ZeroTicks, true)),
            Worst(Metric(rows, unknown, "food", Term.ThresholdDepthTicks, true), Metric(rows, unknown, "rest", Term.ThresholdDepthTicks, true)),
            Worst(Metric(rows, unknown, "food", Term.ThresholdExposureTicks, true), Metric(rows, unknown, "rest", Term.ThresholdExposureTicks, true)),
            Metric(rows, unknown, "food", Term.ThresholdExposureTicks), Metric(rows, unknown, "rest", Term.ThresholdExposureTicks)
        };
        var thermalKnown = outcome.ThermalExposure.Count > 0;
        double degreeTicks = 0;
        foreach (var t in outcome.ThermalExposure) { thermalKnown &= t.UnknownTicks == 0; degreeTicks += t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks; }
        if (!thermalKnown) unknown.Add("unassessed_thermal_exposure");
        var needsVector = new Fact<double>[]
        {
            outcome.StationaryThermalRelief.IsKnown ? Known(-outcome.StationaryThermalRelief.Value) : default,
            thermalKnown ? Known(degreeTicks) : default,
            Metric(rows, unknown, "joy", Term.ThresholdExposureTicks, true), Metric(rows, unknown, "joy", Term.ThresholdExposureTicks),
            Metric(rows, unknown, "food", Term.ZeroAtHorizon), Metric(rows, unknown, "rest", Term.ZeroAtHorizon)
        };
        // Requested relief (a floor, a removal) is development. A garment is not: the ledger prices it by the thoughts and armor it
        // changes (#3816), and neither is the floor or removal a priced room owes, which its credit counts (#3796). Planned
        // development counts what the schedule completes, whichever root built it, each physical site once (#3334), but no
        // planned room's site or roof, priced or not: a room wins a portfolio on the ledger only (#3828).
        var pricedRelief = context is null ? null : PlannedRoomCredits.PricedRelief(context);
        Dictionary<string, double>? relief = null;
        foreach (var c in commitments)
        {
            if (c.Outcome?.Requirements is not { } requirements) continue;
            foreach (var r in requirements.Relief)
                if (pricedRelief?.Contains(r.Need) != true) (relief ??= new(StringComparer.Ordinal))[r.Need] = relief.GetValueOrDefault(r.Need) + r.Fraction;
        }
        double delivered = 0;
        if (relief is not null) foreach (var fraction in relief.Values) delivered += Math.Min(1, fraction);
        var plannedUnits = context is null ? null : PlannedRoomCredits.PlannedUnits(context);
        var units = 0;
        foreach (var unit in outcome.RequestedDevelopment) if (plannedUnits?.Contains(unit) != true) units++;
        var development = delivered + units;
        var moodKnown = rows.Count > 0; var breakKnown = true;
        double exposure = 0, pressure = 0, breaks = 0;
        foreach (var needs in rows.Values)
        {
            if (needs.Mood is not { } m || !m.TargetExposureTicks.IsKnown || !m.PressureTicks.IsKnown) { moodKnown = false; break; }
            exposure += m.TargetExposureTicks.Value; pressure += m.PressureTicks.Value;
            if (m.BreakRisk.TryGetValue(out var risk)) breaks += risk; else breakKnown = false;
        }
        breakKnown &= moodKnown;
        if (!moodKnown) unknown.Add("unassessed_mood");
        if (!breakKnown) unknown.Add("mental_break_probability_unassessed");
        foreach (var finding in outcome.Support.Findings) unknown.Add(finding);
        foreach (var m in outcome.Maintenance) foreach (var finding in m.Findings) unknown.Add(finding);
        unknown.Sort(StringComparer.Ordinal);
        var distinct = 0;
        for (var i = 0; i < unknown.Count; i++) if (i == 0 || unknown[i] != unknown[i - 1]) unknown[distinct++] = unknown[i];
        var unknownRows = new string[distinct];
        unknown.CopyTo(0, unknownRows, 0, distinct);
        return new(EquatableArray.Wrap(urgent), EquatableArray.Wrap(needsVector), moodKnown ? Known(exposure) : default,
            moodKnown ? Known(pressure) : default, development, outcome.Plan.LaborCost, EquatableArray.Wrap(unknownRows))
        {
            BreakRisk = breakKnown ? Known(breaks) : default, ThermalOccupancy = outcome.ThermalOccupancy, Support = outcome.Support, Maintenance = outcome.Maintenance,
        };
    }

    /// <summary>
    /// One feasible outcome as the search ranks it (#3723): the terms read from the outcome and the value's floors at construction, the
    /// ledger and so the complete <see cref="Vector"/> only when a comparison reaches the ledger or a consumer needs the vector. A
    /// complete vector wraps as one already priced, so <see cref="CompareConsequences(ForecastConsequenceVector, ForecastConsequenceVector)"/>
    /// and the search share one comparator.
    /// </summary>
    internal sealed class StagedConsequences
    {
        private readonly ForecastValue.Staged? staged;
        private ForecastConsequenceVector vector;

        private StagedConsequences(ForecastConsequenceVector vector, ForecastValue.Staged? staged) { this.vector = vector; this.staged = staged; }

        internal static StagedConsequences Of(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments, ForecastCoupledContext context,
            double horizonTicks, ForecastCoupledOutcome reference, HarmDetail? referenceHarm)
        {
            var terms = ConsequenceTerms(outcome, commitments, context);
            return new(terms, ForecastValue.Stage(outcome, terms.Urgent[0], context, horizonTicks, reference, referenceHarm, commitments));
        }

        /// <summary>A vector already priced, as the comparator reads it.</summary>
        internal static StagedConsequences Complete(ForecastConsequenceVector vector) => new(vector, null);

        public ForecastFloor UrgentZero => staged?.UrgentZero ?? vector.Value.UrgentZero;
        public ForecastFloor Death => staged?.Death ?? vector.Value.Death;
        public ForecastFloor BreakRisk => staged?.BreakRisk ?? vector.Value.BreakRisk;
        public ForecastValuePolicy Policy => staged?.Policy ?? vector.Value.Policy;
        public IReadOnlyList<ForecastGoalContribution> GoalContributions => staged?.GoalContributions ?? vector.Value.Ledger.GoalContributions;
        public double Labor => vector.Labor;
        /// <summary>The terms read from the outcome alone, valid before the ledger is priced; the complete vector once it is.</summary>
        public ForecastConsequenceVector Terms => vector;
        public bool LedgerPriced => staged is null || staged.LedgerPriced;
        /// <summary>The complete vector, pricing the ledger on the first read.</summary>
        public ForecastConsequenceVector Vector
        {
            get
            {
                if (staged is { LedgerPriced: false } s) vector = vector with { Value = s.Explanation };
                return vector;
            }
        }
        /// <summary>The complete vector when the ledger has been priced, else null: what evidence reports without pricing more.</summary>
        public ForecastConsequenceVector? VectorIfPriced => LedgerPriced ? Vector : null;
    }

    /// <summary>
    /// Negative when <paramref name="a"/> is better. The order (#3522): the safety floors (a broken urgent-zero floor, compared
    /// to <see cref="UrgentZeroResolutionTicks"/>, then the
    /// death floor (#3546), then a broken break-risk floor; the least excess wins when both sides break one); the capability-days ledger, lower loss
    /// winning over the terms both sides assess, a difference within <see cref="ForecastValuePolicy.TieResolution"/> being a tie;
    /// then the terms with no mechanical conversion yet, in the order below. Zero leaves the incumbent rule and labor to the caller.
    /// </summary>
    internal static int CompareConsequences(ForecastConsequenceVector a, ForecastConsequenceVector b)
        => CompareConsequences(StagedConsequences.Complete(a), StagedConsequences.Complete(b), out _);

    // The comparator's stages, as evidence names them (ForecastPortfolioEvidence.StageOf, #3723); the floors' names are the goal routes' (#3503).
    internal const string UrgentZeroStage = "urgent_zero";
    internal const string DeathFloorStage = "death_risk_floor";
    internal const string BreakRiskStage = "break_risk_cap";
    internal const string LedgerStage = "ledger";
    internal const string IncumbentStage = "incumbent";
    internal const string LaborStage = "labor";

    /// <summary>
    /// The comparator over staged consequences (#3723): the same order, pricing both sides' ledgers only when the floors tie, so a
    /// comparison that needs a term evaluates it and the ledger compares the terms both sides assess. <paramref name="stage"/> names
    /// the stage that decided, or the last stage when the result is 0 (the caller's incumbent and labor rule then decide).
    /// </summary>
    internal static int CompareConsequences(StagedConsequences a, StagedConsequences b, out string stage)
    {
        stage = UrgentZeroStage;
        var zero = a.UrgentZero.Excess - b.UrgentZero.Excess;
        if (Math.Abs(zero) >= UrgentZeroResolutionTicks) return zero.CompareTo(0);
        stage = DeathFloorStage;
        var floor = a.Death.Excess.CompareTo(b.Death.Excess); if (floor != 0) return floor;
        stage = BreakRiskStage;
        floor = a.BreakRisk.Excess.CompareTo(b.BreakRisk.Excess); if (floor != 0) return floor;
        stage = LedgerStage;
        if (a.Vector.Value.Ledger.LossAgainst(b.Vector.Value.Ledger) is var delta && Math.Abs(delta) > a.Policy.TieResolution)
            return delta.CompareTo(0);
        var (x, y) = (a.Terms, b.Terms);
        stage = "urgent_first_care";
        { var rank = x.Support.UrgentFirstCareTicks.CompareTo(y.Support.UrgentFirstCareTicks); if (rank != 0) return rank; }
        // Relief is measured against each outcome's own unimproved rooms, so it ranks even when the sleeps differ in time.
        stage = "thermal_relief";
        if (x.Needs[0].IsKnown && y.Needs[0].IsKnown)
        { var rank = x.Needs[0].Value.CompareTo(y.Needs[0].Value); if (rank != 0) return rank; }
        stage = "care_batches";
        var care = x.Support.PendingCareBatches.CompareTo(y.Support.PendingCareBatches); if (care != 0) return care;
        if (x.Support.CareServices.Length != 0 && x.Support.CareServices == y.Support.CareServices)
        {
            stage = "tend_quality";
            { var rank = y.Support.TendQuality.CompareTo(x.Support.TendQuality); if (rank != 0) return rank; }
            stage = "care_room";
            if (x.Support.CareRoom is CareRoomRead.Assessed(var xRoom) && y.Support.CareRoom is CareRoomRead.Assessed(var yRoom))
            { var rank = xRoom.CompareTo(yRoom); if (rank != 0) return rank; }
            stage = "surgery_room";
            { var rank = y.Support.SurgeryRoomFactor.CompareTo(x.Support.SurgeryRoomFactor); if (rank != 0) return rank; }
        }
        stage = "maintenance_xp";
        double missing = 0, missingOther = 0;
        for (var i = 0; i < x.Maintenance.Count && i < y.Maintenance.Count; i++)
        {
            var (first, second) = (x.Maintenance[i], y.Maintenance[i]);
            if (first.Requirement != second.Requirement || first.MissingXp is not ForecastMaintenanceGap.Projected(var xp)
                || second.MissingXp is not ForecastMaintenanceGap.Projected(var otherXp)) continue;
            missing += xp; missingOther += otherXp;
        }
        var maintenanceRank = missing.CompareTo(missingOther);
        if (maintenanceRank != 0) return maintenanceRank;
        // Joy threshold exposure (the longest, then the total) stays here until the mood model prices the recreation thought.
        for (var index = 2; index <= 3; index++)
        {
            stage = index == 2 ? "joy_exposure" : "joy_total";
            if (!x.Needs[index].IsKnown || !y.Needs[index].IsKnown) continue;
            var rank = x.Needs[index].Value.CompareTo(y.Needs[index].Value); if (rank != 0) return rank;
        }
        stage = "development";
        var development = y.Development.CompareTo(x.Development); if (development != 0) return development;
        stage = "unsupplied_demand";
        var supply = x.Support.UnsuppliedDemand.CompareTo(y.Support.UnsuppliedDemand); if (supply != 0) return supply;
        stage = "first_care";
        return x.Support.FirstCareTicks.CompareTo(y.Support.FirstCareTicks);
    }

    public static PawnActivities? StartCoupledActivity(ForecastCoupledSelection selection,
        ForecastCoupledCandidates candidates, ulong authority, string method, long tick)
    {
        if (selection.Selection.Selected.Any(id => candidates.Incumbents.Any(c => c.Id == id)))
            throw new DomainException("retained incumbent is forecast-only; observe release before fresh binding");
        return Methods.Binding.CoordinatedActivityBinding.StartSelected(selection.Selection, candidates.Context.Bindings, authority, method, tick, candidates.Context.PlannedSites);
    }

    /// <summary>The unselected colonists whose observed needs stay above every supported threshold for at least a tick: the
    /// no-change choice is safe for them. They are not held: they run vanilla work (#3669).</summary>
    private static EquatableArray<string> DeferUnselectedPawns(ForecastCoupledContext context, ForecastActivityPlan plan)
    {
        var horizon = context.Activities.Deadline;
        if (context.Resources.Claims.Truncated || !double.IsFinite(horizon) || horizon <= 0) return [];
        var limit = Math.Min(600d, horizon);
        var selected = plan.Allocations.Select(a => a.Pawn).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var pawn in context.Needs.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            if (pawn.State != "eligible" || pawn.VoluntaryControlBlockers.Count != 0 || selected.Contains(pawn.Pawn)
                || context.Resources.Claims.Pawns.Contains(pawn.Pawn) || pawn.Needs.Count == 0) continue;
            var capacity = ForecastNeeds.NeedsWorkCapacity(context.Resources, pawn.Pawn, limit).Capacity;
            if (capacity.Windows is not ForecastCapacityWindows.Read(var windows) || !windows.Any(w => w.Start <= 0 && w.End > 0)) continue;
            var duration = Math.Min(limit, windows.Where(w => w.Start <= 0).Max(w => w.End));
            foreach (var need in pawn.Needs)
            {
                var (level, fall, threshold) = (need.Level, need.FallPerTick, need.Threshold);
                if (!double.IsFinite(level)
                    || !double.IsFinite(fall) || !double.IsFinite(threshold) || threshold < 0 || threshold > 1 || fall < 0 || level <= threshold)
                { duration = 0; break; }
                if (fall > 0) duration = Math.Min(duration, (level - threshold) / fall);
            }
            if (duration < 1) continue;
            result.Add(pawn.Pawn);
        }
        return EquatableArray.From(result);
    }
}
