namespace RimGovernor.Brain.Policy;

/// <summary>Inspectable consequences, ordered lexicographically; no conversion between wellbeing and labor.</summary>
public sealed record ForecastConsequenceVector(EquatableArray<Fact<double>> Urgent, EquatableArray<Fact<double>> Needs,
    Fact<double> MoodExposure, Fact<double> MoodDepth, double Development, double Labor,
    EquatableArray<string> Unknown)
{
    public string ThermalOccupancy { get; init; } = "";
    public ForecastSupportConsequences Support { get; init; } = new();
}

public sealed record ForecastCoupledSelection(ForecastActivityOutcomeComparison Selection,
    ForecastCoupledOutcome Baseline, ForecastCoupledOutcome Selected,
    ForecastConsequenceVector BaselineVector, ForecastConsequenceVector SelectedVector,
    EquatableArray<ForecastPortfolioEvidence> Rejected, double Horizon, bool GenerationTruncated)
{
    public int CompositionExpansions { get; init; }
}
public sealed record ForecastPortfolioEvidence(EquatableArray<string> Commitments,
    ForecastConsequenceVector? Consequences, string Reason);

public static partial class PolicyFunctions
{
    /// <summary>One bounded subset search, evaluated by the same ColonyForecast trajectory as its baseline.
    /// Empty/continue is evaluated first. Partial portfolios remain expandable regardless of isolated value.</summary>
    public static ForecastCoupledSelection CompareActivityOutcomes(ColonyForecastRequest request,
        ForecastCoupledCandidates candidates, int budget = 16384)
    {
        if (budget < 0) throw new DomainException("negative portfolio budget");
        request = request with { Coupled = candidates.Context, Proposed = new() };
        var baselineScenario = ForecastCoupledScenarioOf(request, ForecastCommitmentsById(request.Commitments)).Coupled!;
        var baseline = baselineScenario.Alternatives.Count > 0 ? baselineScenario.Alternatives[0]
            : new ForecastCoupledOutcome(new([], 0, 0), default, [], new Dictionary<string, ForecastNeedsResult>(), []);
        foreach (var candidate in baselineScenario.Alternatives.Where(a => a.Feasible == Known(true)))
        {
            var rank = CompareConsequences(CoupledConsequences(candidate, request.Commitments), CoupledConsequences(baseline, request.Commitments));
            if (baseline.Feasible != Known(true) || rank < 0 || rank == 0 && candidate.Plan.LaborCost < baseline.Plan.LaborCost)
                baseline = candidate;
        }
        var best = baseline;
        var baselineVector = CoupledConsequences(baseline, request.Commitments);
        var bestVector = baselineVector;
        var selected = Array.Empty<string>();
        var findings = new SortedSet<string>(candidates.Findings, StringComparer.Ordinal);
        findings.UnionWith(baselineScenario.Findings);
        if (baseline.Feasible != Known(true)) findings.Add("baseline_unassessed_or_infeasible");
        var evidence = new List<ForecastPortfolioEvidence>();
        // Establish the incumbent before spending optional search; report that mandatory work as well.
        var expansions = baselineScenario.Expansions; var truncated = baselineScenario.Truncated;
        var alternatives = candidates.Alternatives.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
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
        bool Evaluate(ForecastCommitment[] additions)
        {
            if (expansions >= budget) { truncated = true; return false; }
            expansions++;
            var commitments = request.Commitments.Concat(additions).ToArray();
            if (!CompatiblePortfolio(commitments)) return true;
            var remaining = budget - expansions;
            if (remaining == 0) { truncated = true; return false; }
            var scenario = ForecastCoupledScenarioOf(request with { Coupled = candidates.Context with
            { Activities = candidates.Context.Activities with { ExpansionBudget = Math.Min(4096, remaining) } } }, ForecastCommitmentsById(commitments)).Coupled!;
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
                var rank = CompareConsequences(vector, bestVector);
                // Keeping current work wins true ties; labor breaks ties between new feasible plans.
                if (best.Feasible != Known(true) || rank < 0 || rank == 0 && selected.Length > 0 && vector.Labor < bestVector.Labor)
                { best = outcome; bestVector = vector; selected = additions.Select(c => c.Id).ToArray(); }
                else evidence.Add(new(EquatableArray.From(additions.Select(c => c.Id)), vector, "consequence_or_incumbent"));
            }
            return expansions < budget;
        }
        // Breadth by cardinality prevents one deep branch from hiding every independent source.
        // No value-based pruning: two individually useless contributions can remain complementary.
        bool Search(int start, int left, List<ForecastCommitment> partial)
        {
            if (left == 0) return Evaluate(partial.ToArray());
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
        for (var size = 1; size <= alternatives.Length; size++)
            if (!Search(0, size, [])) { truncated = true; break; }
        if (truncated) findings.Add("portfolio_search_truncated");
        if (candidates.Truncated) findings.Add("portfolio_generation_truncated");
        findings.Add("beyond_horizon_unassessed");
        return new(new(selected, best.Plan, default, findings.ToArray(), expansions, truncated), baseline, best,
            baselineVector, bestVector, EquatableArray.From(evidence), request.HorizonDays * GameTime.TicksPerDay, candidates.Truncated)
        { CompositionExpansions = candidates.CompositionExpansions };
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
            .SelectMany(c => c.Outcome!.Requirements!.Relief).GroupBy(r => r.Need).Sum(g => Math.Min(1, g.Sum(r => r.Fraction)))
            + outcome.CompletedDevelopmentSites.Count;
        var moods = outcome.Needs.Values.Select(n => n.Mood).ToArray();
        var moodKnown = moods.Length > 0 && moods.All(m => m is not null && m.TargetExposureTicks.IsKnown && m.PressureTicks.IsKnown);
        if (!moodKnown) unknown.Add("unassessed_mood");
        unknown.Add("serious_harm_probability_unassessed");
        unknown.UnionWith(outcome.Support.Findings);
        return new(EquatableArray.From(urgent), EquatableArray.From(needsVector), moodKnown ? Known(moods.Sum(m => m!.TargetExposureTicks.Value)) : default,
            moodKnown ? Known(moods.Sum(m => m!.PressureTicks.Value)) : default,
            development, outcome.Plan.LaborCost, EquatableArray.From(unknown)) { ThermalOccupancy = outcome.ThermalOccupancy, Support = outcome.Support };
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
        var development = b.Development.CompareTo(a.Development); if (development != 0) return development;
        var supply = a.Support.UnsuppliedDemand.CompareTo(b.Support.UnsuppliedDemand); if (supply != 0) return supply;
        return a.Support.FirstCareTicks.CompareTo(b.Support.FirstCareTicks);
    }

    public static PawnActivities? StartCoupledActivity(ForecastCoupledSelection selection,
        ForecastCoupledCandidates candidates, ulong authority, string method, long tick)
        => Methods.Binding.CoordinatedActivityBinding.Start(selection.Selection, candidates.Context.Bindings, authority, method, tick);
}
