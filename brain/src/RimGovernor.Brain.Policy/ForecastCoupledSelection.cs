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
    /// <summary>The eligible colonists the selection left unassigned while their needs stand above every threshold: standby takes them
    /// (#3624), with its valued hauls, and a later review reconsiders them. While any stands, the admin/work-assignments Need is
    /// owed (<see cref="RoundsRules.WorkAssignmentsOwed"/>), so the standby and haul Methods under it are not released as unneeded.</summary>
    public EquatableArray<string> Deferred { get; init; }
    /// <summary>The search's expansions per phase it reached (#3563): baseline, warm, seed, unserved, extend, confirm, sweep.</summary>
    public EquatableArray<ForecastSearchPhase> SearchPhases { get; init; }
    /// <summary>The phase in which the chosen portfolio first became the leader; "baseline" when nothing beat it.</summary>
    public string SelectedPhase { get; init; } = "";
    /// <summary>"" without a previous selection; "seeded" when the previous selection resolved to current roots and was evaluated first; "missing" when none of its ids resolved.</summary>
    public string WarmStart { get; init; } = "";
}
/// <summary>
/// One phase the search reached: its scheduler expansions and, for the profile (#3556), its wall, the schedule
/// searches it ran (<see cref="Evaluations"/>; a subset whose schedule was already searched at the bound runs none) with
/// their wall (<see cref="ScheduleMs"/>; the rest of <see cref="Ms"/> is keying, feasibility and ranking), and the
/// evaluations whose every outcome was infeasible or unassessed (<see cref="Infeasible"/>) or whose construction
/// lacked supported service relief (<see cref="Unsupported"/>).
/// </summary>
public sealed record ForecastSearchPhase(string Phase, int Expansions)
{
    public int Evaluations { get; init; }
    public double Ms { get; init; }
    public double ScheduleMs { get; init; }
    public int Infeasible { get; init; }
    public int Unsupported { get; init; }
    /// <summary>Extension evaluations that swapped a root exclusive with a leader member in for it, rather than adding a compatible root.</summary>
    public int Swaps { get; init; }
    /// <summary>Evaluations that made a new leader.</summary>
    public int Improved { get; init; }
}
public sealed record ForecastPortfolioEvidence(EquatableArray<string> Commitments,
    ForecastConsequenceVector? Consequences, string Reason);

public sealed partial record ForecastCoupledSelection
{
    private static readonly System.Diagnostics.ActivitySource Spans = new("github.com/davidarcher/RimGovernor/policy");

    /// <summary>A selection finding <c>goal_infeasible:&lt;goal need id&gt;:&lt;floor&gt;</c> (#3503): every evaluated route that progresses the goal breaks that floor where the baseline holds it.</summary>
    public const string GoalInfeasiblePrefix = "goal_infeasible:";

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
    /// and standby is the fallback only when nothing startable is worth selecting.</summary>
    public static ForecastCoupledSelection CompareActivityOutcomes(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, int budget = 65536, IReadOnlyList<string>? warmStart = null, Func<double>? clockMs = null)
    {
        var selection = CompareOnce(request, candidates, budget, warmStart, clockMs);
        var unplaced = candidates.Context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var allocations = selection.Selected.Plan.Allocations;
        if (unplaced.Count == 0 || allocations.Length == 0 || !allocations.All(a => unplaced.Contains(a.Activity))) return selection;
        var reaching = ReachingActivities(candidates.Context.Activities.Activities, unplaced);
        var placed = candidates with { Alternatives = EquatableArray.From(candidates.Alternatives.Where(c => !c.ActivityCompletions.Any(reaching.Contains))) };
        if (placed.Alternatives.Count == candidates.Alternatives.Count) return selection;
        var orders = CompareOnce(request, placed, budget, warmStart: null, clockMs);
        var findings = new SortedSet<string>(selection.Selection.Findings, StringComparer.Ordinal) { PlannedUnplacedReselected };
        findings.UnionWith(orders.Selection.Findings);
        return selection with
        {
            Selection = selection.Selection with
            {
                Selected = [.. selection.Selection.Selected, .. orders.Selection.Selected],
                Plan = selection.Selection.Plan! with { Allocations = [.. allocations, .. orders.Selection.Plan?.Allocations ?? []] },
                Findings = findings.ToArray(),
            },
            Deferred = orders.Deferred,
        };
    }

    /// <summary>A selection finding: the chosen portfolio was unplaced planned sites alone, so the comparison without them adds the review's orders (#3557).</summary>
    public const string PlannedUnplacedReselected = "planned_unplaced_reselected";

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
        ForecastCoupledCandidates candidates, int budget, IReadOnlyList<string>? warmStart, Func<double>? clockMs)
    {
        if (budget < 0) throw new DomainException("negative portfolio budget");
        // The in-game cost of the comparator: the review runs its planners in parallel, so only this span times it.
        using var span = Spans.StartActivity("coupled.select");
        span?.SetTag("rimgovernor.roots", candidates.Alternatives.Count + candidates.Incumbents.Count);
        span?.SetTag("rimgovernor.budget", budget);
        request = request with { Coupled = candidates.Context, Proposed = new() };
        var baselineCommitments = request.Commitments.Concat(candidates.Incumbents).ToArray();
        var incumbentIds = candidates.Incumbents.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        // One cache for the whole search (#3468): the context's indexes and every pawn's timelines are shared across portfolios.
        var cache = new ForecastCoupled.CoupledSearchCache();
        var baselineScenario = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById(baselineCommitments), cache).Coupled!;
        var baseline = baselineScenario.Alternatives.Count > 0 ? baselineScenario.Alternatives[0]
            : new ForecastCoupledOutcome(new([], 0, 0), default, new Dictionary<string, ForecastNeedsResult>(), []);
        // Every outcome's wellbeing is priced against this one outcome, so the deltas between outcomes do not depend on which is the baseline.
        var reference = baseline;
        var horizonTicks = request.HorizonDays * GameTime.TicksPerDay;
        // Relief a goal injected is valued by the goal's ledger term, not as a unit of development (#3503).
        var goalRelief = candidates.Context.GoalValues.SelectMany(g => g.Relief.Select(r => r.Need)).ToHashSet(StringComparer.Ordinal);
        ForecastConsequenceVector Consequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments)
            => CoupledConsequences(outcome, commitments, candidates.Context, horizonTicks, reference, goalRelief);
        // Per goal, the least-broken floor over the evaluated outcomes that progress it, while no such outcome holds every floor the baseline holds (#3503).
        var goalRoutes = new Dictionary<string, (bool Open, string Floor, double Excess)>(StringComparer.Ordinal);
        void NoteGoalRoutes(ForecastConsequenceVector vector, ForecastConsequenceVector against)
        {
            var zero = vector.Value.UrgentZero.Excess - against.Value.UrgentZero.Excess;
            var cap = vector.Value.BreakRisk.Excess - against.Value.BreakRisk.Excess;
            var death = vector.Value.Death.Excess - against.Value.Death.Excess;
            foreach (var contribution in vector.Value.Ledger.GoalContributions)
            {
                var route = zero >= UrgentZeroResolutionTicks ? ("urgent_zero", zero) : death > 0 ? ("death_risk_floor", death) : cap > 0 ? ("break_risk_cap", cap) : ("", 0d);
                if (!goalRoutes.TryGetValue(contribution.Goal, out var seen) || route.Item1 == "" || !seen.Open && route.Item2 < seen.Excess)
                    goalRoutes[contribution.Goal] = (route.Item1 == "" || seen.Open, route.Item1, route.Item2);
            }
        }
        foreach (var candidate in baselineScenario.Alternatives.Where(a => a.Feasible == Known(true)))
        {
            var rank = CompareConsequences(Consequences(candidate, baselineCommitments), Consequences(baseline, baselineCommitments));
            if (baseline.Feasible != Known(true) || rank < 0 || rank == 0 && candidate.Plan.LaborCost < baseline.Plan.LaborCost)
                baseline = candidate;
        }
        var best = baseline;
        var baselineVector = Consequences(baseline, baselineCommitments);
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
        var expansions = baselineScenario.Expansions; var truncated = baselineScenario.Truncated;
        // Expansions per phase and the phase that found the chosen subset (#3563).
        var phases = new List<ForecastSearchPhase>(); var phase = "baseline"; var phaseStart = 0; var selectedPhase = "baseline";
        // The phase's profile (#3556): wall, schedule searches and their wall, infeasible and unsupported evaluations.
        double Now() => clockMs?.Invoke() ?? 0; var phaseAt = Now(); var phaseEvaluations = 0; var phaseScheduleMs = 0.0; var phaseInfeasible = 0; var phaseUnsupported = 0;
        var phaseSwaps = 0; var phaseImproved = 0;
        void Phase(string next)
        {
            var now = Now();
            phases.Add(new(phase, expansions - phaseStart)
            {
                Evaluations = phaseEvaluations, Ms = now - phaseAt,
                ScheduleMs = phaseScheduleMs, Infeasible = phaseInfeasible, Unsupported = phaseUnsupported,
                Swaps = phaseSwaps, Improved = phaseImproved,
            });
            (phaseStart, phase, phaseAt, phaseEvaluations, phaseScheduleMs, phaseInfeasible, phaseUnsupported, phaseSwaps, phaseImproved) = (expansions, next, now, 0, 0.0, 0, 0, 0, 0);
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
        for (var i = 0; i < alternatives.Length; i++)
        {
            var closure = closures[i] = new ulong[taskWords];
            void Include(string id)
            {
                if (!taskIndex.TryGetValue(id, out var at)) throw new DomainException("unknown commitment activity: " + id);
                if (Bits.Has(closure, at)) return;
                Bits.Set(closure, at);
                foreach (var prerequisite in tasks[at].Prerequisites) Include(prerequisite);
            }
            foreach (var completion in alternatives[i].ActivityCompletions) Include(completion);
        }
        static bool GraphOnly(ForecastCommitment c) => c.ActivityCompletions.Count > 0 && c.Outcome is null
            && c.StockChanges.Count == 0 && c.MaterialClaims.Count == 0 && c.EquipmentClaims.Count == 0
            && c.UnsupportedEffects.Count == 0 && c.Construction is null && !c.AlternativeLaborCost.IsKnown && c.MoodMemories.Count == 0
            && c.SituationalChanges.Count == 0 && c.ComfortShifts.Count == 0 && c.OutfitChanges.Count == 0;
        static bool PlannedSiteRoot(ForecastCommitment c) => c.Outcome?.Need == ForecastPlannedSite.DevelopmentNeed && GraphOnly(c with { Outcome = null });
        var unplaced = candidates.Context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        // Per root: whether it is graph-only, keys a schedule by id (direct effects or claims; a retained incumbent) and exempts
        // its closure from the relief gate: a planned site root (#3651), or a root that requests relief.
        var graphOnly = new bool[alternatives.Length]; var keyed = new bool[alternatives.Length]; var exempting = new bool[alternatives.Length];
        for (var i = 0; i < alternatives.Length; i++)
        {
            graphOnly[i] = GraphOnly(alternatives[i]);
            var plannedSite = PlannedSiteRoot(alternatives[i]);
            keyed[i] = !graphOnly[i] && !plannedSite || incumbentIds.Contains(alternatives[i].Id);
            exempting[i] = plannedSite || alternatives[i].Outcome?.Requirements is { Relief.Count: > 0 };
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
            evaluated[key] = bound;
            if (expansions >= budget) { truncated = true; return false; }
            expansions++;
            if (!schedules.TryGetValue(schedule, out var known) || known.Bound < bound)
            {
                var remaining = budget - expansions;
                if (remaining == 0) { truncated = true; return false; }
                var searchAt = Now();
                var searched = ForecastCoupled.ForecastCoupledScenarioOf(request, ColonyForecastRules.ForecastCommitmentsById(commitments), cache,
                    expansionBudget: Math.Min(bound, remaining)).Coupled!;
                phaseScheduleMs += Now() - searchAt; phaseEvaluations++;
                expansions += searched.Expansions;
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
                evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), null, "infeasible_or_unassessed"));
                foreach (var reason in scenario.Alternatives.SelectMany(a => a.Findings)) findings.Add(reason.Reason + ":" + reason.Commitment);
            }
            foreach (var outcome in feasible)
            {
                var vector = Consequences(outcome, commitments);
                NoteGoalRoutes(vector, baselineVector);
                // Physical feasibility does not certify optional investment. Partial shells retain
                // unknown exposure; they cannot win by hiding it. Keep expanding complementary roots.
                // Construction in the closure of a selected planned site root (#3651) or of a root with its own relief is
                // exempt, and so is a planned room's roof cell when the schedule finishes that room's roof.
                var roofed = candidates.Context.Shelters.Where(r => r.PlannedRoom != 0 && r.RoofCells.Count != 0
                        && outcome.RequestedDevelopment.Contains(FormattableString.Invariant($"planned/room{r.PlannedRoom}/roof")))
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
                    evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector, "construction_without_supported_service_relief" + string.Concat(outcome.Findings.Where(f => f.Reason.StartsWith("thermal_relief_unassessed", StringComparison.Ordinal)).Select(f => ":" + f.Reason))
                        + (outcome.StationaryThermalRelief.IsKnown ? ":relief=" + outcome.StationaryThermalRelief.Value : "")));
                    continue;
                }
                var rank = CompareConsequences(vector, bestVector);
                // Consequence ties never release a retained incumbent to save nominal labor.
                var retainsSelectedIncumbents = selected.Where(incumbentIds.Contains).All(id => additions.Any(c => c.Id == id));
                if (best.Feasible != Known(true) || rank < 0 || rank == 0 && !bestIsBaseline && retainsSelectedIncumbents && vector.Labor < bestVector.Labor)
                {
                    var ids = additions.Select(c => c.Id).ToArray();
                    if (!ids.SequenceEqual(selected, StringComparer.Ordinal)) { selectedPhase = phase; phaseImproved++; }
                    best = outcome; bestVector = vector; selected = ids; bestIsBaseline = false;
                }
                else evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector, "consequence_or_incumbent"));
            }
            return expansions < budget;
        }
        // Breadth by cardinality prevents one deep branch from hiding every independent source.
        // No value-based pruning: two individually useless contributions can remain complementary.
        // The seed pass may stop starting evaluations once it has spent its share; that is a hand-off, not truncation.
        var seedLimit = int.MaxValue; var seedSpent = false; var leafBound = PortfolioBound;
        var partialBits = new ulong[rootWords];
        bool Search(int start, int left, List<ForecastCommitment> partial)
        {
            if (left == 0)
            {
                if (expansions >= seedLimit) { seedSpent = true; return false; }
                return Evaluate(partial.ToArray(), leafBound);
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
            // Seed pass: screened single roots until a quarter of the budget is spent (every root if that is cheaper).
            seedLimit = budget / SeedShare; leafBound = ScreenBound;
            continueSearch = Search(0, 1, []) || seedSpent;
            seedLimit = int.MaxValue; leafBound = PortfolioBound;
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
        // the budget left can finish it: the subsets not yet evaluated at the mean cost of an evaluation so far. Otherwise
        // the search ends converged at the confirmed leader, truncated as before (the sweep was never finishing either).
        if (continueSearch)
        {
            var evaluations = Math.Max(1, evaluatedByKey.Count);
            var affordable = (budget - expansions) / Math.Max(1, expansions / evaluations);
            var subsets = alternatives.Length >= 62 ? long.MaxValue : (1L << alternatives.Length) - 1 - evaluations;
            if (subsets > affordable) { continueSearch = false; truncated = true; }
        }
        if (continueSearch) Phase("sweep");
        for (var size = 1; continueSearch && size <= alternatives.Length; size++)
            if (!Search(0, size, [])) { truncated = true; break; }
        Phase("end");
        foreach (var (reasons, cut) in screenedByKey.Values) { findings.UnionWith(reasons); truncated |= cut; }
        if (truncated) findings.Add("portfolio_search_truncated");
        if (candidates.Truncated) findings.Add("portfolio_generation_truncated");
        foreach (var (goal, route) in goalRoutes)
            if (!route.Open) findings.Add(GoalInfeasiblePrefix + goal + ":" + route.Floor);
        findings.Add("beyond_horizon_unassessed");
        // The harm term and the death floor are reported unassessed, with the specific finding, when the chosen outcome could not price
        // them (#3546); a break risk no mood threshold supports leaves that floor unchecked.
        if (!bestVector.Value.Ledger.Harm.IsKnown)
        {
            findings.Add("harm_capability_unassessed");
            findings.UnionWith(bestVector.Value.Ledger.HarmFindings);
        }
        if (!bestVector.Value.Death.Assessed) findings.Add("death_floor_unassessed");
        if (!bestVector.Value.BreakRisk.Assessed) findings.Add("break_floor_unassessed");
        var deferred = DeferUnselectedPawns(candidates.Context, best.Plan);
        span?.SetTag("rimgovernor.expansions", expansions);
        span?.SetTag("rimgovernor.schedules", schedulesByKey.Count);
        span?.SetTag("rimgovernor.truncated", truncated);
        return new(new(selected, best.Plan, default, findings.ToArray(), expansions, truncated), baseline, best,
            baselineVector, bestVector, EquatableArray.From(evidence), request.HorizonDays * GameTime.TicksPerDay, candidates.Truncated)
        { CompositionExpansions = candidates.CompositionExpansions,
            ServiceInvestments = ForecastServiceInvestments.ServiceInvestmentsOf(baseline, best, candidates.Context, request.HorizonDays * GameTime.TicksPerDay),
            Deferred = deferred,
            SearchPhases = EquatableArray.From(phases), SelectedPhase = selectedPhase, WarmStart = warmStartState };
    }

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

    public static ForecastConsequenceVector CoupledConsequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments,
        IReadOnlySet<string>? goalRelief = null)
        => CoupledConsequences(outcome, commitments, null, 0, null, goalRelief);

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

    /// <summary>The vector, priced by the ledger against <paramref name="reference"/> when a <paramref name="context"/> is given (#3522).
    /// Relief in <paramref name="goalRelief"/> is a goal's, valued by the ledger's goal term, not as development (#3503).</summary>
    internal static ForecastConsequenceVector CoupledConsequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments,
        ForecastCoupledContext? context, double horizonTicks, ForecastCoupledOutcome? reference, IReadOnlySet<string>? goalRelief)
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
        // Delivered clothing is development, not wearing or physiological thermal protection. Planned development counts
        // what the schedule completes, whichever root built it, each physical site once (#3334), but not a priced room's (#3651).
        Dictionary<string, double>? relief = null;
        foreach (var c in commitments)
        {
            if (c.Outcome?.Requirements is not { } requirements) continue;
            foreach (var r in requirements.Relief)
                if (goalRelief?.Contains(r.Need) != true) (relief ??= new(StringComparer.Ordinal))[r.Need] = relief.GetValueOrDefault(r.Need) + r.Fraction;
        }
        double delivered = 0;
        if (relief is not null) foreach (var fraction in relief.Values) delivered += Math.Min(1, fraction);
        var development = delivered + UnpricedDevelopment(outcome.RequestedDevelopment, context);
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
            Value = context is null ? ForecastValueExplanation.Empty : ForecastValue.Explain(outcome, urgent[0], context, horizonTicks, reference!, commitments)
        };
    }

    /// <summary>
    /// The completed planned units the development tiebreak counts (#3651, #3647 decision 3): every unit except a site, or the
    /// <c>planned/room{R}/roof</c> unit, of a room whose role (ForecastCoupledContext.PlannedRoomRoles) is priced
    /// (ForecastCoupledContext.PricedRoomRoles). Every unit counts without a context or a priced role.
    /// </summary>
    private static int UnpricedDevelopment(EquatableArray<string> units, ForecastCoupledContext? context)
    {
        if (context is null || context.PricedRoomRoles.Count == 0 || units.Count == 0) return units.Count;
        var priced = context.PlannedRoomRoles.Where(r => context.PricedRoomRoles.Contains(r.Value)).Select(r => r.Key).ToHashSet();
        if (priced.Count == 0) return units.Count;
        var skipped = context.PlannedSites.Where(s => priced.Contains(s.Room)).Select(s => s.Site)
            .Concat(priced.Select(room => FormattableString.Invariant($"planned/room{room}/roof"))).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var unit in units) if (!skipped.Contains(unit)) count++;
        return count;
    }

    /// <summary>
    /// Negative when <paramref name="a"/> is better. The order (#3522): the safety floors (a broken urgent-zero floor, compared
    /// to <see cref="UrgentZeroResolutionTicks"/>, then the
    /// death floor (#3546), then a broken break-risk floor; the least excess wins when both sides break one); the capability-days ledger, lower loss
    /// winning over the terms both sides assess, a difference within <see cref="ForecastValuePolicy.TieResolution"/> being a tie;
    /// then the terms with no mechanical conversion yet, in the order below. Zero leaves the incumbent rule and labor to the caller.
    /// </summary>
    internal static int CompareConsequences(ForecastConsequenceVector a, ForecastConsequenceVector b)
    {
        var zero = a.Value.UrgentZero.Excess - b.Value.UrgentZero.Excess;
        if (Math.Abs(zero) >= UrgentZeroResolutionTicks) return zero.CompareTo(0);
        var floor = a.Value.Death.Excess.CompareTo(b.Value.Death.Excess); if (floor != 0) return floor;
        floor = a.Value.BreakRisk.Excess.CompareTo(b.Value.BreakRisk.Excess); if (floor != 0) return floor;
        if (a.Value.Ledger.LossAgainst(b.Value.Ledger).TryGetValue(out var delta) && Math.Abs(delta) > a.Value.Policy.TieResolution)
            return delta.CompareTo(0);
        if (a.Support.UrgentFirstCareTicks.IsKnown && b.Support.UrgentFirstCareTicks.IsKnown)
        { var rank = a.Support.UrgentFirstCareTicks.Value.CompareTo(b.Support.UrgentFirstCareTicks.Value); if (rank != 0) return rank; }
        // Relief is measured against each outcome's own unimproved rooms, so it ranks even when the sleeps differ in time.
        if (a.Needs[0].IsKnown && b.Needs[0].IsKnown)
        { var rank = a.Needs[0].Value.CompareTo(b.Needs[0].Value); if (rank != 0) return rank; }
        var care = a.Support.PendingCareBatches.CompareTo(b.Support.PendingCareBatches); if (care != 0) return care;
        if (a.Support.CareServices.Length != 0 && a.Support.CareServices == b.Support.CareServices)
        {
            if (a.Support.TendQuality.IsKnown && b.Support.TendQuality.IsKnown)
            { var rank = b.Support.TendQuality.Value.CompareTo(a.Support.TendQuality.Value); if (rank != 0) return rank; }
            if (a.Support.CareRoomFactor.IsKnown && b.Support.CareRoomFactor.IsKnown)
            { var rank = a.Support.CareRoomFactor.Value.CompareTo(b.Support.CareRoomFactor.Value); if (rank != 0) return rank; }
            if (a.Support.SurgeryRoomFactor.IsKnown && b.Support.SurgeryRoomFactor.IsKnown)
            { var rank = b.Support.SurgeryRoomFactor.Value.CompareTo(a.Support.SurgeryRoomFactor.Value); if (rank != 0) return rank; }
        }
        double missing = 0, missingOther = 0;
        for (var i = 0; i < a.Maintenance.Count && i < b.Maintenance.Count; i++)
        {
            var (first, second) = (a.Maintenance[i], b.Maintenance[i]);
            if (first.Requirement != second.Requirement || !first.MissingXp.IsKnown || !second.MissingXp.IsKnown) continue;
            missing += first.MissingXp.Value; missingOther += second.MissingXp.Value;
        }
        var maintenanceRank = missing.CompareTo(missingOther);
        if (maintenanceRank != 0) return maintenanceRank;
        // Joy threshold exposure (the longest, then the total) stays here until the mood model prices the recreation thought.
        for (var index = 2; index <= 3; index++)
        {
            if (!a.Needs[index].IsKnown || !b.Needs[index].IsKnown) continue;
            var rank = a.Needs[index].Value.CompareTo(b.Needs[index].Value); if (rank != 0) return rank;
        }
        var development = b.Development.CompareTo(a.Development); if (development != 0) return development;
        var supply = a.Support.UnsuppliedDemand.CompareTo(b.Support.UnsuppliedDemand); if (supply != 0) return supply;
        return a.Support.FirstCareTicks.CompareTo(b.Support.FirstCareTicks);
    }

    public static PawnActivities? StartCoupledActivity(ForecastCoupledSelection selection,
        ForecastCoupledCandidates candidates, ulong authority, string method, long tick)
    {
        if (selection.Selection.Selected.Any(id => candidates.Incumbents.Any(c => c.Id == id)))
            throw new DomainException("retained incumbent is forecast-only; observe release before fresh binding");
        return Methods.Binding.CoordinatedActivityBinding.StartSelected(selection.Selection, candidates.Context.Bindings, authority, method, tick, candidates.Context.PlannedSites);
    }

    /// <summary>The unselected colonists whose observed needs stay above every supported threshold for at least a tick: the
    /// no-change choice is safe for them. They are not held: standby takes them, with its valued hauls (#3624).</summary>
    private static EquatableArray<string> DeferUnselectedPawns(ForecastCoupledContext context, ForecastActivityPlan plan)
    {
        if (context.Resources.Claims.Truncated
            || !context.Activities.Deadline.TryGetValue(out var horizon) || !double.IsFinite(horizon) || horizon <= 0) return [];
        var limit = Math.Min(600d, horizon);
        var selected = plan.Allocations.Select(a => a.Pawn).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var pawn in context.Needs.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            if (pawn.State != "eligible" || pawn.VoluntaryControlBlockers.Count != 0 || pawn.Location is null || selected.Contains(pawn.Pawn)
                || context.Resources.Claims.Pawns.Contains(pawn.Pawn) || pawn.Needs.Count == 0) continue;
            var capacity = ForecastNeeds.NeedsWorkCapacity(context.Resources, pawn.Pawn, limit).Capacity;
            if (!capacity.Windows.TryGetValue(out var windows) || !windows.Any(w => w.Start <= 0 && w.End > 0)) continue;
            var duration = Math.Min(limit, windows.Where(w => w.Start <= 0).Max(w => w.End));
            foreach (var need in pawn.Needs)
            {
                if (!need.Level.TryGetValue(out var level) || !need.FallPerTick.TryGetValue(out var fall)
                    || !need.Threshold.TryGetValue(out var threshold) || !double.IsFinite(level)
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
