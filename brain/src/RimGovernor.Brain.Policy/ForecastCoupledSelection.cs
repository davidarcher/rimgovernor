namespace RimGovernor.Brain.Policy;

/// <summary>Inspectable consequences, ordered lexicographically; no conversion between wellbeing and labor.</summary>
public sealed record ForecastConsequenceVector(EquatableArray<Fact<double>> Urgent, EquatableArray<Fact<double>> Needs,
    Fact<double> MoodExposure, Fact<double> MoodDepth, double Development, double Labor,
    EquatableArray<string> Unknown)
{
    public string ThermalOccupancy { get; init; } = "";
    public EquatableArray<ForecastMaintenanceConsequence> Maintenance { get; init; }
    public ForecastSupportConsequences Support { get; init; } = new();
}

public sealed record ForecastCoupledSelection(ForecastActivityOutcomeComparison Selection,
    ForecastCoupledOutcome Baseline, ForecastCoupledOutcome Selected,
    ForecastConsequenceVector BaselineVector, ForecastConsequenceVector SelectedVector,
    EquatableArray<ForecastPortfolioEvidence> Rejected, double Horizon, bool GenerationTruncated)
{
    public int CompositionExpansions { get; init; }
    public EquatableArray<ForecastServiceInvestment> ServiceInvestments { get; init; }
    public EquatableArray<ForecastDeferredPawn> Deferred { get; init; }
}
public sealed record ForecastDeferredPawn(string Pawn, Cell Location, long Until);
public sealed record ForecastPortfolioEvidence(EquatableArray<string> Commitments,
    ForecastConsequenceVector? Consequences, string Reason);

public static partial class PolicyFunctions
{
    private const int SeedShare = 4;
    /// <summary>Scheduler bound of a screening evaluation. The depth-first schedule search reaches its first complete
    /// plans within a few expansions per activity and spends the rest of a larger bound varying the last tasks, so a
    /// portfolio's consequences are ranked from the first few dozen plans; the 4,096 bound confirms the final choice.</summary>
    private const int ScreenBound = 64;
    private const int PortfolioBound = 4096;

    /// <summary>One bounded subset search, evaluated by the same ColonyForecast trajectory as its baseline.
    /// Empty/continue is evaluated first. Partial portfolios remain expandable regardless of isolated value.
    /// Order: single roots, screened, for up to a quarter of the budget; the leader plus each other root,
    /// screened, repeated from each new leader until none improves it; the final leader at the full
    /// per-portfolio bound; then the full cardinality sweep (singles, pairs, ...) at that bound, skipping
    /// subsets already evaluated at it.</summary>
    public static ForecastCoupledSelection CompareActivityOutcomes(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, int budget = 16384)
    {
        if (budget < 0) throw new DomainException("negative portfolio budget");
        request = request with { Coupled = candidates.Context, Proposed = new() };
        var baselineCommitments = request.Commitments.Concat(candidates.Incumbents).ToArray();
        var incumbentIds = candidates.Incumbents.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var baselineScenario = ForecastCoupledScenarioOf(request, ForecastCommitmentsById(baselineCommitments)).Coupled!;
        var baseline = baselineScenario.Alternatives.Count > 0 ? baselineScenario.Alternatives[0]
            : new ForecastCoupledOutcome(new([], 0, 0), default, [], new Dictionary<string, ForecastNeedsResult>(), []);
        foreach (var candidate in baselineScenario.Alternatives.Where(a => a.Feasible == Known(true)))
        {
            var rank = CompareConsequences(CoupledConsequences(candidate, baselineCommitments), CoupledConsequences(baseline, baselineCommitments));
            if (baseline.Feasible != Known(true) || rank < 0 || rank == 0 && candidate.Plan.LaborCost < baseline.Plan.LaborCost)
                baseline = candidate;
        }
        var best = baseline;
        var baselineVector = CoupledConsequences(baseline, baselineCommitments);
        var bestVector = baselineVector;
        var selected = incumbentIds.Order(StringComparer.Ordinal).ToArray();
        var bestIsBaseline = true;
        var findings = new SortedSet<string>(candidates.Findings, StringComparer.Ordinal);
        findings.UnionWith(baselineScenario.Findings);
        if (baseline.Feasible != Known(true)) findings.Add("baseline_unassessed_or_infeasible");
        var evidence = new List<ForecastPortfolioEvidence>();
        // Establish the incumbent before spending optional search; report that mandatory work as well.
        var expansions = baselineScenario.Expansions; var truncated = baselineScenario.Truncated;
        var alternatives = candidates.Alternatives.Concat(candidates.Incumbents).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (alternatives.Select(c => c.Id).Distinct().Count() != alternatives.Length)
            throw new DomainException("duplicate portfolio candidate");
        // A graph-only root adds nothing when another selected root already includes all its
        // physical work. Drop that redundant subset before scheduling; keep all provider tasks
        // in the containing root's prerequisite closure. Roots with direct effects/claims stay.
        var activities = candidates.Context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var closures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var alternative in alternatives)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal);
            void Include(string id)
            {
                if (!closure.Add(id)) return;
                if (!activities.TryGetValue(id, out var activity)) throw new DomainException("unknown commitment activity: " + id);
                foreach (var prerequisite in activity.Prerequisites) Include(prerequisite);
            }
            foreach (var completion in alternative.ActivityCompletions) Include(completion);
            closures.Add(alternative.Id, closure);
        }
        static bool GraphOnly(ForecastCommitment c) => c.ActivityCompletions.Count > 0 && c.Outcome is null
            && c.StockChanges.Count == 0 && c.MaterialClaims.Count == 0 && c.EquipmentClaims.Count == 0
            && c.UnsupportedEffects.Count == 0 && c.Construction is null && !c.AlternativeLaborCost.IsKnown;
        var redundantPairs = alternatives.Where(GraphOnly).SelectMany(a => alternatives.Where(b => a.Id != b.Id
            && closures[a.Id].IsSubsetOf(closures[b.Id])).Select(b => (Contained: a.Id, Container: b.Id))).ToHashSet();
        // The traversal reaches a subset by more than one phase; evaluating it again at a bound it already got
        // would only repeat its cost. A larger bound is a new evaluation.
        var evaluated = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Evaluate(ForecastCommitment[] additions, int bound = PortfolioBound)
        {
            var key = string.Join('\n', additions.Select(c => c.Id));
            if (evaluated.TryGetValue(key, out var done) && done >= bound) return true;
            evaluated[key] = bound;
            if (expansions >= budget) { truncated = true; return false; }
            expansions++;
            var commitments = request.Commitments.Concat(additions).ToArray();
            if (!CompatiblePortfolio(commitments)) return true;
            var remaining = budget - expansions;
            if (remaining == 0) { truncated = true; return false; }
            var scenario = ForecastCoupledScenarioOf(request with { Coupled = candidates.Context with
            { Activities = candidates.Context.Activities with { ExpansionBudget = Math.Min(bound, remaining) } } }, ForecastCommitmentsById(commitments)).Coupled!;
            expansions += scenario.Expansions;
            findings.UnionWith(scenario.Findings);
            if (scenario.Truncated) truncated = true;
            var feasible = scenario.Alternatives.Where(a => a.Feasible == Known(true)).ToArray();
            if (feasible.Length == 0)
            {
                evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), null, "infeasible_or_unassessed"));
                foreach (var reason in scenario.Alternatives.SelectMany(a => a.Findings)) findings.Add(reason.Reason + ":" + reason.Commitment);
            }
            foreach (var outcome in feasible)
            {
                var vector = CoupledConsequences(outcome, commitments);
                // Physical feasibility does not certify optional investment. Partial shells retain
                // unknown exposure; they cannot win by hiding it. Keep expanding complementary roots.
                if (outcome.Plan.Allocations.Any(a => candidates.Context.Bindings.TryGetValue((a.Activity, a.Pawn), out var binding)
                    && (binding.SiteDefinition.Length != 0 || binding.Job == "BuildRoof"))
                    && (!outcome.StationaryThermalRelief.IsKnown || outcome.StationaryThermalRelief.Value <= 0))
                {
                    evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector, "construction_without_supported_service_relief"));
                    continue;
                }
                var rank = CompareConsequences(vector, bestVector);
                // Consequence ties never release a retained incumbent to save nominal labor.
                var retainsSelectedIncumbents = selected.Where(incumbentIds.Contains).All(id => additions.Any(c => c.Id == id));
                if (best.Feasible != Known(true) || rank < 0 || rank == 0 && !bestIsBaseline && retainsSelectedIncumbents && vector.Labor < bestVector.Labor)
                { best = outcome; bestVector = vector; selected = additions.Select(c => c.Id).ToArray(); bestIsBaseline = false; }
                else evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector, "consequence_or_incumbent"));
            }
            return expansions < budget;
        }
        // Breadth by cardinality prevents one deep branch from hiding every independent source.
        // No value-based pruning: two individually useless contributions can remain complementary.
        // The seed pass may stop starting evaluations once it has spent its share; that is a hand-off, not truncation.
        var seedLimit = int.MaxValue; var seedSpent = false; var leafBound = PortfolioBound;
        bool Search(int start, int left, List<ForecastCommitment> partial)
        {
            if (left == 0)
            {
                if (expansions >= seedLimit) { seedSpent = true; return false; }
                return Evaluate(partial.ToArray(), leafBound);
            }
            for (var i = start; i <= alternatives.Length - left; i++)
            {
                if (!CompatiblePortfolio(partial.Append(alternatives[i]))) continue;
                if (partial.Any(c => redundantPairs.Contains((c.Id, alternatives[i].Id))
                    || redundantPairs.Contains((alternatives[i].Id, c.Id)))) continue;
                partial.Add(alternatives[i]);
                var more = Search(i + 1, left - 1, partial);
                partial.RemoveAt(partial.Count - 1);
                if (!more) return false;
            }
            return true;
        }
        // Extend the best portfolio found so far with each other root, in root order, before the
        // pairwise sweep: when single roots exhaust the budget, the only way to reach a second
        // contribution is to add it to the leader rather than to enumerate every pair.
        bool Extend(string[] leader)
        {
            var members = leader.ToHashSet(StringComparer.Ordinal);
            var rank = alternatives.Select((c, i) => (c.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
            var seed = alternatives.Where(c => members.Contains(c.Id)).ToArray();
            foreach (var root in alternatives.Where(c => !members.Contains(c.Id)))
            {
                var portfolio = seed.Append(root).OrderBy(c => rank[c.Id]).ToArray();
                if (!CompatiblePortfolio(portfolio)) continue;
                if (seed.Any(c => redundantPairs.Contains((c.Id, root.Id)) || redundantPairs.Contains((root.Id, c.Id)))) continue;
                if (!Evaluate(portfolio, ScreenBound)) return false;
            }
            return true;
        }
        var continueSearch = candidates.Incumbents.Count == 0 || Evaluate([]);
        if (continueSearch)
        {
            // Seed pass: screened single roots until a quarter of the budget is spent (every root if that is cheaper).
            seedLimit = budget / SeedShare; leafBound = ScreenBound;
            continueSearch = Search(0, 1, []) || seedSpent;
            seedLimit = int.MaxValue; leafBound = PortfolioBound;
            if (!continueSearch) truncated = true;
        }
        if (continueSearch && !bestIsBaseline)
        {
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
                var members = selected.ToHashSet(StringComparer.Ordinal);
                if (!Evaluate(alternatives.Where(c => members.Contains(c.Id)).ToArray())) { continueSearch = false; truncated = true; }
            }
        }
        for (var size = 1; continueSearch && size <= alternatives.Length; size++)
            if (!Search(0, size, [])) { truncated = true; break; }
        if (truncated) findings.Add("portfolio_search_truncated");
        if (candidates.Truncated) findings.Add("portfolio_generation_truncated");
        findings.Add("beyond_horizon_unassessed");
        var deferred = DeferUnselectedPawns(candidates.Context, best.Plan, request.Origin.ObservedTick);
        var deferredCapacity = Math.Max(0, 32 - best.Plan.Allocations.Length);
        if (deferred.Count > deferredCapacity) findings.Add("deferred_pawn_limit");
        return new(new(selected, best.Plan, default, findings.ToArray(), expansions, truncated), baseline, best,
            baselineVector, bestVector, EquatableArray.From(evidence), request.HorizonDays * GameTime.TicksPerDay, candidates.Truncated)
        { CompositionExpansions = candidates.CompositionExpansions,
            ServiceInvestments = ServiceInvestmentsOf(baseline, best, candidates.Context, request.HorizonDays * GameTime.TicksPerDay),
            Deferred = EquatableArray.From(deferred.Take(deferredCapacity)) };
    }

    private static bool CompatiblePortfolio(IEnumerable<ForecastCommitment> input)
    {
        var rows = input.ToArray();
        return !rows.Where(c => c.ExclusiveOutcome.Length != 0).GroupBy(c => c.ExclusiveOutcome).Any(g => g.Count() > 1)
            && !rows.Any(c => c.Outcome is { } effect && rows.Any(other => c.Id != other.Id
                && (effect.Excludes.Contains(other.Outcome?.Id ?? other.Id) || effect.ExclusiveOutput.Length != 0 && effect.ExclusiveOutput == other.Outcome?.ExclusiveOutput)));
    }

    public static ForecastConsequenceVector CoupledConsequences(ForecastCoupledOutcome outcome, IEnumerable<ForecastCommitment> commitments)
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        Fact<double> Metric(string kind, Func<ForecastNeedMetric, Fact<double>> get, bool maximum = false)
        {
            var values = new List<double>();
            var complete = outcome.Needs.Count > 0;
            foreach (var (pawn, needs) in outcome.Needs)
                foreach (var metric in needs.Needs.Where(n => n.Kind == kind))
                    if (get(metric).TryGetValue(out var number)) values.Add(number);
                    else { unknown.Add("unassessed:" + pawn + ":" + kind); complete = false; }
            return complete ? Known(maximum ? values.DefaultIfEmpty(0).Max() : values.Sum()) : default;
        }
        Fact<double> Worst(Fact<double> a, Fact<double> b) => a.IsKnown && b.IsKnown ? Known(Math.Max(a.Value, b.Value)) : default;
        var urgent = new[] { Worst(Metric("food", n => n.ZeroTicks, true), Metric("rest", n => n.ZeroTicks, true)),
            Worst(Metric("food", n => n.ThresholdDepthTicks, true), Metric("rest", n => n.ThresholdDepthTicks, true)),
            Worst(Metric("food", n => n.ThresholdExposureTicks, true), Metric("rest", n => n.ThresholdExposureTicks, true)),
            Metric("food", n => n.ThresholdExposureTicks), Metric("rest", n => n.ThresholdExposureTicks) };
        var thermalKnown = outcome.ThermalExposure.Count > 0 && outcome.ThermalExposure.All(t => t.UnknownTicks == 0);
        if (!thermalKnown) unknown.Add("unassessed_thermal_exposure");
        var needsVector = new[] { outcome.StationaryThermalRelief.IsKnown ? Known(-outcome.StationaryThermalRelief.Value) : default,
            thermalKnown ? Known(outcome.ThermalExposure.Sum(t => t.AssessedColdDegreeTicks + t.AssessedHotDegreeTicks)) : default,
            Metric("joy", n => n.ThresholdExposureTicks, true), Metric("joy", n => n.ThresholdExposureTicks),
            Metric("food", n => n.FinalLevel.IsKnown ? Known(n.FinalLevel.Value == 0 ? 1.0 : 0.0) : default),
            Metric("rest", n => n.FinalLevel.IsKnown ? Known(n.FinalLevel.Value == 0 ? 1.0 : 0.0) : default) };
        // Delivered clothing is development, not wearing or physiological thermal protection.
        var development = commitments.Where(c => c.Outcome?.Requirements is not null)
            .SelectMany(c => c.Outcome!.Requirements!.Relief).GroupBy(r => r.Need).Sum(g => Math.Min(1, g.Sum(r => r.Fraction)));
        var moods = outcome.Needs.Values.Select(n => n.Mood).ToArray();
        var moodKnown = moods.Length > 0 && moods.All(m => m is not null && m.TargetExposureTicks.IsKnown && m.PressureTicks.IsKnown);
        if (!moodKnown) unknown.Add("unassessed_mood");
        unknown.Add("serious_harm_probability_unassessed");
        unknown.UnionWith(outcome.Support.Findings);
        unknown.UnionWith(outcome.Maintenance.SelectMany(m => m.Findings));
        return new(EquatableArray.From(urgent), EquatableArray.From(needsVector), moodKnown ? Known(moods.Sum(m => m!.TargetExposureTicks.Value)) : default,
            moodKnown ? Known(moods.Sum(m => m!.PressureTicks.Value)) : default,
            development, outcome.Plan.LaborCost, EquatableArray.From(unknown)) { ThermalOccupancy = outcome.ThermalOccupancy, Support = outcome.Support, Maintenance = outcome.Maintenance };
    }

    private static int CompareConsequences(ForecastConsequenceVector a, ForecastConsequenceVector b)
    {
        if (a.Support.UrgentFirstCareTicks.IsKnown && b.Support.UrgentFirstCareTicks.IsKnown)
        { var rank = a.Support.UrgentFirstCareTicks.Value.CompareTo(b.Support.UrgentFirstCareTicks.Value); if (rank != 0) return rank; }
        var sameThermalUse = a.ThermalOccupancy.Length != 0 && a.ThermalOccupancy == b.ThermalOccupancy;
        foreach (var pair in a.Urgent.Zip(b.Urgent).Concat(a.Needs.Zip(b.Needs).Skip(sameThermalUse ? 0 : 1)))
        {
            if (!pair.First.IsKnown || !pair.Second.IsKnown) continue;
            var rank = pair.First.Value.CompareTo(pair.Second.Value); if (rank != 0) return rank;
        }
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
        // Compare room contamination only for the same completed production. Skipping meals is not
        // credited as preventing illness, and unknown health effects do not become clean-room rewards.
        if (a.Support.FoodProduction.Length != 0 && a.Support.FoodProduction == b.Support.FoodProduction
            && a.Support.FoodPoisonExposure.IsKnown && b.Support.FoodPoisonExposure.IsKnown)
        { var rank = a.Support.FoodPoisonExposure.Value.CompareTo(b.Support.FoodPoisonExposure.Value); if (rank != 0) return rank; }
        if (a.MoodExposure.IsKnown && b.MoodExposure.IsKnown)
        { var rank = a.MoodExposure.Value.CompareTo(b.MoodExposure.Value); if (rank != 0) return rank; }
        if (a.MoodDepth.IsKnown && b.MoodDepth.IsKnown)
        { var rank = a.MoodDepth.Value.CompareTo(b.MoodDepth.Value); if (rank != 0) return rank; }
        var maintenance = a.Maintenance.Zip(b.Maintenance).Where(p => p.First.Requirement == p.Second.Requirement
            && p.First.MissingXp.IsKnown && p.Second.MissingXp.IsKnown).ToArray();
        var maintenanceRank = maintenance.Sum(p => p.First.MissingXp.Value).CompareTo(maintenance.Sum(p => p.Second.MissingXp.Value));
        if (maintenanceRank != 0) return maintenanceRank;
        var development = b.Development.CompareTo(a.Development); if (development != 0) return development;
        var supply = a.Support.UnsuppliedDemand.CompareTo(b.Support.UnsuppliedDemand); if (supply != 0) return supply;
        return a.Support.FirstCareTicks.CompareTo(b.Support.FirstCareTicks);
    }

    public static PawnActivities? StartCoupledActivity(ForecastCoupledSelection selection,
        ForecastCoupledCandidates candidates, ulong authority, string method, long tick)
    {
        if (selection.Selection.Selected.Any(id => candidates.Incumbents.Any(c => c.Id == id)))
            throw new DomainException("retained incumbent is forecast-only; observe release before fresh binding");
        return Methods.Binding.CoordinatedActivityBinding.StartWithDeferred(selection, candidates.Context.Bindings, authority, method, tick);
    }

    /// <summary>The no-change choice has a finite observed safe interval, not permission for a native selector.
    /// Reconsider at most every 600 ticks and before the first supported need threshold.</summary>
    private static EquatableArray<ForecastDeferredPawn> DeferUnselectedPawns(ForecastCoupledContext context, ForecastActivityPlan plan, long tick)
    {
        if (context.Resources.Claims.Truncated || context.Needs.Truncated
            || !context.Activities.Deadline.TryGetValue(out var horizon) || !double.IsFinite(horizon) || horizon <= 0) return [];
        var limit = Math.Min(600d, horizon);
        var selected = plan.Allocations.Select(a => a.Pawn).ToHashSet(StringComparer.Ordinal);
        var result = new List<ForecastDeferredPawn>();
        foreach (var pawn in context.Needs.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            if (pawn.State != "eligible" || pawn.VoluntaryControlBlockers.Count != 0 || pawn.Location is not { } location || selected.Contains(pawn.Pawn)
                || context.Resources.Claims.Pawns.Contains(pawn.Pawn) || pawn.Needs.Count == 0) continue;
            var capacity = NeedsWorkCapacity(context.Resources, pawn.Pawn, limit).Capacity;
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
            result.Add(new(pawn.Pawn, location, checked(tick + (long)Math.Floor(duration))));
        }
        return EquatableArray.From(result);
    }
}
