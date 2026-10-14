namespace RimGovernor.Brain.Policy;

/// <summary>Mutually exclusive ways to serve one demand. Value is supplied utility, not a calibrated game score.</summary>
public sealed record ForecastMaterialOption(string Demand, string Id, Fact<double> Value, EquatableArray<Amount> Cost);
public sealed record ForecastMaterialComparison(EquatableArray<ForecastMaterialOption> Selected, double Value,
    int Expansions, bool SearchTruncated, EquatableArray<string> Findings);

public static partial class PolicyFunctions
{
    /// <summary>Joint material deltas against the forecast's shared free budget; every demand can be postponed.</summary>
    public static ForecastMaterialComparison CompareMaterialPlans(IEnumerable<ForecastMaterialOption> options,
        Fact<IReadOnlyDictionary<Resource, Fact<double>>> availability, int expansionBudget = 4096)
    {
        if (expansionBudget < 1) throw new DomainException("positive material search budget required");
        var rows = options.OrderBy(o => o.Demand, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        if (rows.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length
            || rows.Any(o => string.IsNullOrWhiteSpace(o.Demand) || string.IsNullOrWhiteSpace(o.Id)
                || o.Value.TryGetValue(out var v) && (!double.IsFinite(v) || v < 0)
                || o.Cost.Any(c => !ValidResource(c.Resource) || c.Count < 0))) throw new DomainException("invalid material option");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (!availability.TryGetValue(out var budget)) return new([], 0, 0, false, ["unknown_material_budget"]);
        var groups = rows.GroupBy(o => o.Demand, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var selected = new List<ForecastMaterialOption>();
        ForecastMaterialOption[] best = [];
        double bestValue = 0;
        var expansions = 0;
        var truncated = false;
        void Search(int index, double value, Dictionary<Resource, long> used)
        {
            // Any partial selection is a complete alternative with all remaining demands postponed.
            if (value > bestValue) { bestValue = value; best = selected.ToArray(); }
            if (index == groups.Length) return;
            foreach (var option in groups[index])
            {
                if (expansions == expansionBudget) { truncated = true; return; }
                expansions++;
                if (!option.Value.TryGetValue(out var benefit)) { findings.Add("unknown_value:" + option.Id); continue; }
                var next = new Dictionary<Resource, long>(used);
                foreach (var cost in option.Cost) next[cost.Resource] = checked(next.GetValueOrDefault(cost.Resource) + cost.Count);
                if (next.Any(c => !budget.GetValueOrDefault(c.Key, Known(0.0)).IsKnown))
                { findings.Add("unknown_material:" + option.Id); continue; }
                if (!MaterialBudgetCovers(budget, next.Select(c => new Amount(c.Key, c.Value)), 1)) continue;
                if (!double.IsFinite(value + benefit)) throw new DomainException("material value overflow");
                selected.Add(option);
                Search(index + 1, value + benefit, next);
                selected.RemoveAt(selected.Count - 1);
            }
            if (expansions == expansionBudget) { truncated = true; return; }
            expansions++;
            Search(index + 1, value, used);
        }
        Search(0, 0, []);
        return new(EquatableArray.From(best), bestValue, expansions, truncated, EquatableArray.From(findings));
    }
}
