namespace RimGovernor.Brain.Policy;

/// <summary>Alternative relief of one owned need. Benefit is avoided loss in baseline labor ticks,
/// supplied by the caller, not inferred from an activity name. Completion includes all prerequisites.</summary>
public sealed record ForecastActivityOutcome(string Id, string Need, string Completion,
    Fact<double> Deadline, Fact<double> AvoidedLaborLoss);

public sealed record ForecastActivityOutcomeComparison(string[] Selected, ForecastActivityPlan? Plan,
    Fact<double> NetBenefit, string[] Findings, int Expansions, bool SearchTruncated);

public enum ForecastOutcomeObjective { NetLaborBenefit, EarliestDeadline }

public static partial class PolicyFunctions
{
    /// <summary>Compare postponement and combinations using the existing ColonyForecast activity scheduler.
    /// One alternative per need; shared prerequisites execute once and all outcomes share pawn capacity.</summary>
    public static ForecastActivityOutcomeComparison CompareActivityOutcomes(ForecastActivityProblem problem,
        ForecastActivityOutcome[] outcomes, ForecastOutcomeObjective objective = ForecastOutcomeObjective.NetLaborBenefit,
        Func<ForecastActivityOutcome[], ForecastActivityPlan, Fact<bool>>? assess = null)
    {
        ValidateActivityProblem(problem);
        if (!Enum.IsDefined(objective)) throw new DomainException("invalid outcome objective");
        var deadlinesFirst = objective == ForecastOutcomeObjective.EarliestDeadline;
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (outcomes.Length > 16 || outcomes.Select(o => o.Id).Distinct().Count() != outcomes.Length
            || outcomes.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.Need)
                || !tasks.ContainsKey(o.Completion)))
            throw new DomainException("invalid activity outcomes");
        foreach (var fact in outcomes.SelectMany(o => new[] { o.Deadline, o.AvoidedLaborLoss }))
            if (fact.TryGetValue(out var number) && (!double.IsFinite(number) || number < 0))
                throw new DomainException("invalid activity outcome estimate");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var known = outcomes.Where(o =>
        {
            if (o.Deadline.IsKnown && (deadlinesFirst || o.AvoidedLaborLoss.IsKnown)) return true;
            findings.Add("unknown_outcome_estimate:" + o.Id);
            return false;
        }).OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
        var expansions = 0;
        var truncated = false;
        var best = new ForecastActivityOutcomeComparison([], null, deadlinesFirst ? default : Known(0.0), [], 0, false);
        if (deadlinesFirst && !problem.AlternativeLaborCost.IsKnown)
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
            foreach (var outcome in selected) Include(outcome.Completion);
            if (expansions == problem.ExpansionBudget) { truncated = true; return; }
            var benefit = deadlinesFirst ? 0 : selected.Sum(o => o.AvoidedLaborLoss.Value);
            if (!double.IsFinite(benefit)) throw new DomainException("activity outcome benefit overflow");
            var compared = CompareActivityPlans(problem with
            {
                Activities = problem.Activities.Where(t => included.Contains(t.Id)).ToArray(),
                Options = problem.Options.Where(o => included.Contains(o.Activity)).ToArray(),
                AlternativeLaborCost = deadlinesFirst ? problem.AlternativeLaborCost : Known(benefit), ExpansionBudget = problem.ExpansionBudget - expansions,
            });
            expansions += compared.Expansions;
            truncated |= compared.SearchTruncated;
            findings.UnionWith(compared.Findings);
            foreach (var plan in compared.Alternatives)
            {
                if (selected.Any(o => plan.Allocations.Single(a => a.Activity == o.Completion).End > o.Deadline.Value))
                { findings.Add("outcome_deadline"); continue; }
                if (assess is not null)
                {
                    var accepted = assess(selected.ToArray(), plan);
                    if (!accepted.TryGetValue(out var valid)) { findings.Add("unknown_plan_effect"); continue; }
                    if (!valid) { findings.Add("insufficient_plan_effect"); continue; }
                }
                var net = benefit - plan.LaborCost;
                if (deadlinesFirst && plan.LaborCost >= problem.AlternativeLaborCost.Value)
                { findings.Add("labor_ceiling"); continue; }
                var rank = deadlinesFirst ? CompareDeadlines(selected) : net.CompareTo(best.NetBenefit.Value);
                if (rank > 0 || (rank == 0 && best.Plan is { } previous
                    && (plan.LaborCost < previous.LaborCost || (plan.LaborCost == previous.LaborCost && plan.CompletedAt < previous.CompletedAt))))
                    best = new(selected.Select(o => o.Id).ToArray(), plan, deadlinesFirst ? default : Known(net), [], 0, false);
            }
        }
        void Search(int start, List<ForecastActivityOutcome> selected)
        {
            Evaluate(selected);
            for (var i = start; i < known.Length && !truncated; i++)
            {
                if (!Spend()) return;
                if (selected.Any(o => o.Need == known[i].Need)) continue;
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
