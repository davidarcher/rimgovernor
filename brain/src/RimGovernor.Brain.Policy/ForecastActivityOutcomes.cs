namespace RimGovernor.Brain.Policy;

/// <summary>Alternative relief of one owned need. Benefit is avoided loss in baseline labor ticks,
/// supplied by the caller, not inferred from an activity name. Completion includes all prerequisites.</summary>
public sealed record ForecastActivityOutcome(string Id, string Need, string Completion,
    Fact<double> Deadline, Fact<double> AvoidedLaborLoss)
{
    /// <summary>Explicit alternative realizations; demand identity alone never excludes additive contributions.</summary>
    public string ChoiceIdentity { get; init; } = "";
    /// <summary>Comparable progress toward the same need, not credited inventory or avoided loss.</summary>
    public Fact<double> ProgressUnits { get; init; }
    public ForecastRequirementAssessment? Requirements { get; init; }
    /// <summary>Observed ongoing baseline work; its existing claims remain in the supplied availability.</summary>
    public Fact<double> RetainedCompletion { get; init; }
    public string ExclusiveOutput { get; init; } = "";
    public EquatableArray<string> Excludes { get; init; }
}

public sealed record ForecastActivityOutcomeComparison(string[] Selected, ForecastActivityPlan? Plan,
    Fact<double> NetBenefit, string[] Findings, int Expansions, bool SearchTruncated);

public enum ForecastOutcomeObjective { NetLaborBenefit, EarliestDeadline, LowestLaborPerUnit, RequirementRelief }

public static class ForecastActivityOutcomes
{
    /// <summary>Compare postponement and combinations using the existing ColonyForecast activity scheduler.
    /// Explicit choice identities exclude variants; shared prerequisites execute once and all outcomes share pawn capacity.</summary>
    public static ForecastActivityOutcomeComparison CompareActivityOutcomes(ForecastActivityProblem problem,
        ForecastActivityOutcome[] outcomes, ForecastOutcomeObjective objective = ForecastOutcomeObjective.NetLaborBenefit,
        Func<ForecastActivityOutcome[], ForecastActivityPlan, Fact<bool>>? assess = null, bool generationTruncated = false,
        IEnumerable<string>? generationFindings = null)
    {
        ForecastActivities.ValidateActivityProblem(problem, allowEmpty: outcomes.All(o => o.RetainedCompletion.IsKnown));
        if (!Enum.IsDefined(objective)) throw new DomainException("invalid outcome objective");
        var deadlinesFirst = objective == ForecastOutcomeObjective.EarliestDeadline;
        var perUnit = objective == ForecastOutcomeObjective.LowestLaborPerUnit;
        var requirementsFirst = objective == ForecastOutcomeObjective.RequirementRelief;
        var boundedCost = deadlinesFirst || perUnit || requirementsFirst;
        if (perUnit && outcomes.Select(o => o.Need).Distinct().Count() > 1)
            throw new DomainException("per-unit comparison requires alternatives for one need");
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (outcomes.Length > 16 || outcomes.Select(o => o.Id).Distinct().Count() != outcomes.Length
            || outcomes.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Need)
                || !tasks.ContainsKey(o.Completion) && !o.RetainedCompletion.IsKnown))
            throw new DomainException("invalid activity outcomes");
        foreach (var fact in outcomes.SelectMany(o => new[] { o.Deadline, o.AvoidedLaborLoss, o.ProgressUnits, o.RetainedCompletion }))
            if (fact.TryGetValue(out var number) && (!double.IsFinite(number) || number < 0))
                throw new DomainException("invalid activity outcome estimate");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (generationFindings is not null) findings.UnionWith(generationFindings);
        if (generationTruncated) findings.Add("generation_truncated");
        var known = outcomes.Where(o =>
        {
            if (o.Deadline.IsKnown && (boundedCost || o.AvoidedLaborLoss.IsKnown)
                && (!perUnit || (o.ProgressUnits.TryGetValue(out var units) && units > 0))) return true;
            findings.Add("unknown_outcome_estimate:" + o.Id);
            return false;
        }).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var expansions = 0;
        var truncated = false;
        var bestUnitCost = double.PositiveInfinity;
        var bestRequirements = new ForecastRequirementPriority();
        var best = new ForecastActivityOutcomeComparison([], null, boundedCost ? default : Known(0.0), [], 0, false);
        if (boundedCost && !problem.AlternativeLaborCost.IsKnown)
            return best with { Findings = [.. findings, "unknown_labor_ceiling"] };
        if (deadlinesFirst && outcomes.GroupBy(o => o.Need).Any(g => g.Select(o => o.Deadline).Distinct().Count() != 1))
            throw new DomainException("alternatives for one need require the same deadline");
        var deadlines = known.Select(o => o.Deadline.Value).Distinct().Order().ToArray();
        int CompareDeadlines(List<ForecastActivityOutcome> selected)
        {
            foreach (var deadline in deadlines)
            {
                var delta = selected.Count(o => o.Deadline.Value == deadline)
                    - known.Count(o => best.Selected.Contains(o.Id) && o.Deadline.Value == deadline);
                if (delta != 0) return delta;
            }
            return 0;
        }
        bool Spend()
        {
            if (expansions == problem.ExpansionBudget) { truncated = true; return false; }
            expansions++;
            return true;
        }
        void Evaluate(List<ForecastActivityOutcome> selected)
        {
            if (selected.Count == 0) return; // Postponement is always the zero-cost baseline.
            if (!Spend()) return;
            var included = new HashSet<string>(StringComparer.Ordinal);
            void Include(string id)
            {
                if (!included.Add(id)) return;
                foreach (var prerequisite in tasks[id].Prerequisites) Include(prerequisite);
            }
            foreach (var outcome in selected.Where(o => !o.RetainedCompletion.IsKnown)) Include(outcome.Completion);
            if (expansions == problem.ExpansionBudget) { truncated = true; return; }
            var benefit = boundedCost ? 0 : selected.Sum(o => o.AvoidedLaborLoss.Value);
            if (!double.IsFinite(benefit)) throw new DomainException("activity outcome benefit overflow");
            var compared = included.Count == 0 ? null : ForecastActivities.CompareActivityPlans(problem with
            {
                Activities = problem.Activities.Where(t => included.Contains(t.Id)).ToArray(),
                Options = problem.Options.Where(o => included.Contains(o.Activity)).ToArray(),
                AlternativeLaborCost = boundedCost ? problem.AlternativeLaborCost : Known(benefit), ExpansionBudget = problem.ExpansionBudget - expansions,
            });
            expansions += compared?.Expansions ?? 0;
            truncated |= compared?.SearchTruncated ?? false;
            findings.UnionWith(compared?.Findings ?? []);
            foreach (var plan in compared?.Alternatives ?? [new ForecastActivityPlan([], 0, 0)])
            {
                if (selected.Any(o => (o.RetainedCompletion.TryGetValue(out var retained) ? retained
                    : plan.Allocations.Single(a => a.Activity == o.Completion).End) > o.Deadline.Value))
                { findings.Add("outcome_deadline"); continue; }
                if (assess is not null)
                {
                    var accepted = assess(selected.ToArray(), plan);
                    if (!accepted.TryGetValue(out var valid)) { findings.Add("unknown_plan_effect"); continue; }
                    if (!valid) { findings.Add("insufficient_plan_effect"); continue; }
                }
                var net = benefit - plan.LaborCost;
                if (boundedCost && plan.LaborCost >= problem.AlternativeLaborCost.Value)
                { findings.Add("labor_ceiling"); continue; }
                var unitCost = perUnit ? plan.LaborCost / selected.Single().ProgressUnits.Value : 0;
                var requirements = requirementsFirst ? ForecastRequirements.RequirementPriorityOf(selected.Select(o =>
                    new ForecastMaterialOption(o.Need, o.Id, Known(0.0), []) { Requirements = o.Requirements }), findings) : null;
                if (requirementsFirst && requirements is null) continue;
                var rank = requirementsFirst
                    ? requirements!.BetterThan(bestRequirements, 0, 0) ? 1 : bestRequirements.BetterThan(requirements!, 0, 0) ? -1 : 0
                    : perUnit ? bestUnitCost.CompareTo(unitCost) : deadlinesFirst ? CompareDeadlines(selected) : net.CompareTo(best.NetBenefit.Value);
                if (rank > 0 || (rank == 0 && best.Plan is { } previous
                    && (plan.LaborCost < previous.LaborCost || (plan.LaborCost == previous.LaborCost && plan.CompletedAt < previous.CompletedAt))))
                {
                    best = new(selected.Select(o => o.Id).ToArray(), plan, boundedCost ? default : Known(net), [], 0, false);
                    bestUnitCost = unitCost;
                    if (requirements is not null) bestRequirements = requirements;
                }
            }
        }
        void Search(int start, List<ForecastActivityOutcome> selected)
        {
            Evaluate(selected);
            for (var i = start; i < known.Length && !truncated; i++)
            {
                if (!Spend()) return;
                if (perUnit && selected.Count != 0 || selected.Any(o => known[i].ChoiceIdentity.Length != 0 && o.ChoiceIdentity == known[i].ChoiceIdentity || known[i].ExclusiveOutput.Length != 0 && o.ExclusiveOutput == known[i].ExclusiveOutput
                    || o.Excludes.Contains(known[i].Id) || known[i].Excludes.Contains(o.Id))) continue;
                selected.Add(known[i]);
                Search(i + 1, selected);
                selected.RemoveAt(selected.Count - 1);
            }
        }
        Search(0, []);
        if (truncated) findings.Add("search_truncated");
        return best with { Findings = findings.ToArray(), Expansions = expansions, SearchTruncated = truncated };
    }
}
