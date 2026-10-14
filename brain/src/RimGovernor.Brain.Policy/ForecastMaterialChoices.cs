namespace RimGovernor.Brain.Policy;

/// <summary>Mutually exclusive ways to serve one demand. Value is supplied utility, not a calibrated game score.</summary>
public sealed record ForecastMaterialOption(string Demand, string Id, Fact<double> Value, EquatableArray<Amount> Cost)
{
    public ForecastProductionEstimate? Estimate { get; init; }
    public ForecastRequirementAssessment? Requirements { get; init; }
}
public sealed record ForecastMaterialComparison(EquatableArray<ForecastMaterialOption> Selected, double Value,
    int Expansions, bool SearchTruncated, EquatableArray<string> Findings)
{
    public ForecastMaterialValue? Valuation { get; init; }
    public ForecastRequirementPriority Priority { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>Joint material deltas against the forecast's shared free budget; every demand can be postponed.</summary>
    public static ForecastMaterialComparison CompareMaterialPlans(IEnumerable<ForecastMaterialOption> options,
        Fact<IReadOnlyDictionary<Resource, Fact<double>>> availability, int expansionBudget = 4096,
        ForecastMaterialValuation? valuation = null)
    {
        if (expansionBudget < 1) throw new DomainException("positive material search budget required");
        var rows = options.OrderBy(o => o.Demand, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        if (rows.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length
            || rows.Any(o => string.IsNullOrWhiteSpace(o.Demand) || string.IsNullOrWhiteSpace(o.Id)
                || o.Value.TryGetValue(out var v) && (!double.IsFinite(v) || v < 0)
                || o.Cost.Any(c => !ValidResource(c.Resource) || c.Count < 0))) throw new DomainException("invalid material option");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (valuation is not null && rows.Any(o => o.Requirements is not null))
            throw new DomainException("requirement priorities and scalar utility are separate objectives");
        if (!availability.TryGetValue(out var budget)) return new([], 0, 0, false, ["unknown_material_budget"]);
        var groups = rows.GroupBy(o => o.Demand, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var selected = new List<ForecastMaterialOption>();
        ForecastMaterialOption[] best = [];
        double bestValue = 0;
        var bestPriority = new ForecastRequirementPriority();
        ForecastMaterialValue? bestValuation = valuation is null ? null : new(0, 0, 0);
        var expansions = 0;
        var truncated = false;
        void Search(int index, double value, Dictionary<Resource, long> used, ForecastMaterialValue? priced, ForecastRequirementPriority priority)
        {
            // Any partial selection is a complete alternative with all remaining demands postponed.
            if (priority.BetterThan(bestPriority, value, bestValue))
            { bestValue = value; best = selected.ToArray(); bestValuation = priced; bestPriority = priority; }
            if (index == groups.Length) return;
            foreach (var option in groups[index])
            {
                if (expansions == expansionBudget) { truncated = true; return; }
                expansions++;
                double benefit = 0;
                if (valuation is null && !option.Value.TryGetValue(out benefit)) { findings.Add("unknown_value:" + option.Id); continue; }
                var next = new Dictionary<Resource, long>(used);
                foreach (var cost in option.Cost) next[cost.Resource] = checked(next.GetValueOrDefault(cost.Resource) + cost.Count);
                if (next.Any(c => !budget.GetValueOrDefault(c.Key, Known(0.0)).IsKnown))
                { findings.Add("unknown_material:" + option.Id); continue; }
                if (!MaterialBudgetCovers(budget, next.Select(c => new Amount(c.Key, c.Value)), 1)) continue;
                if (!double.IsFinite(value + benefit)) throw new DomainException("material value overflow");
                selected.Add(option);
                var evaluated = valuation is null ? null : ValueMaterialPlan(selected, valuation, findings);
                var requirements = RequirementPriorityOf(selected, findings);
                if ((valuation is null || evaluated is not null) && requirements is not null)
                    Search(index + 1, evaluated?.Net ?? value + benefit, next, evaluated, requirements);
                selected.RemoveAt(selected.Count - 1);
            }
            if (expansions == expansionBudget) { truncated = true; return; }
            expansions++;
            Search(index + 1, value, used, priced, priority);
        }
        Search(0, 0, [], bestValuation, bestPriority);
        return new(EquatableArray.From(best), bestValue, expansions, truncated, EquatableArray.From(findings)) { Valuation = bestValuation, Priority = bestPriority };
    }
}
