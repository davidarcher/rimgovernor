namespace RimGovernor.Brain.Policy;

/// <summary>Mutually exclusive ways to serve one demand. Value is supplied utility, not a calibrated game score; a supplied
/// valuation, when the comparison has one, prices the plan instead.</summary>
public sealed record ForecastMaterialOption(string Demand, string Id, double Value, EquatableArray<Amount> Cost)
{
    public ForecastProductionEstimate? Estimate { get; init; }
    public ForecastRequirementAssessment? Requirements { get; init; }
}
public sealed record ForecastMaterialComparison(EquatableArray<ForecastMaterialOption> Selected,
    int Expansions, bool SearchTruncated, EquatableArray<string> Findings)
{
    public ForecastRequirementPriority Priority { get; init; } = new();
}

public static class ForecastMaterialChoices
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
                || !double.IsFinite(o.Value) || o.Value < 0
                || o.Cost.Any(c => !AdmissionRules.ValidResource(c.Resource) || c.Count < 0))) throw new DomainException("invalid material option");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (valuation is not null && rows.Any(o => o.Requirements is not null))
            throw new DomainException("requirement priorities and scalar utility are separate objectives");
        if (!availability.TryGetValue(out var budget)) return new([], 0, false, ["unknown_material_budget"]);
        var groups = rows.GroupBy(o => o.Demand, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var selected = new List<ForecastMaterialOption>();
        ForecastMaterialOption[] best = [];
        double bestValue = 0;
        var bestPriority = new ForecastRequirementPriority();
        var expansions = 0;
        var truncated = false;
        void Search(int index, double value, Dictionary<Resource, long> used, ForecastRequirementPriority priority)
        {
            // Any partial selection is a complete alternative with all remaining demands postponed.
            if (priority.BetterThan(bestPriority, value, bestValue))
            { bestValue = value; best = selected.ToArray(); bestPriority = priority; }
            if (index == groups.Length) return;
            foreach (var option in groups[index])
            {
                if (expansions == expansionBudget) { truncated = true; return; }
                expansions++;
                var benefit = valuation is null ? option.Value : 0;
                var next = new Dictionary<Resource, long>(used);
                foreach (var cost in option.Cost) next[cost.Resource] = checked(next.GetValueOrDefault(cost.Resource) + cost.Count);
                if (next.Any(c => !budget.GetValueOrDefault(c.Key, Known(0.0)).IsKnown))
                { findings.Add("unknown_material:" + option.Id); continue; }
                if (!ProductionRules.MaterialBudgetCovers(budget, next.Select(c => new Amount(c.Key, c.Value)), 1)) continue;
                if (!double.IsFinite(value + benefit)) throw new DomainException("material value overflow");
                selected.Add(option);
                var evaluated = valuation is null ? null : ForecastMaterialValue.ValueMaterialPlan(selected, valuation, findings);
                var requirements = ForecastRequirements.RequirementPriorityOf(selected, findings);
                if ((valuation is null || evaluated is not null) && requirements is not null)
                    Search(index + 1, evaluated?.Net ?? value + benefit, next, requirements);
                selected.RemoveAt(selected.Count - 1);
            }
            if (expansions == expansionBudget) { truncated = true; return; }
            expansions++;
            Search(index + 1, value, used, priority);
        }
        Search(0, 0, [], bestPriority);
        return new(EquatableArray.From(best), expansions, truncated, EquatableArray.From(findings)) { Priority = bestPriority };
    }
}
