using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>RandomMain's weighted category redraw, including failed forced ThreatBig attempts.</summary>
internal static class IncidentCategoryDraw
{
    internal sealed record Category(string Name, double Weight, IReadOnlyDictionary<string, double> Incidents)
    {
        internal double Success { get; init; } = Incidents.Values.Sum();
    }

    // Each recursion removes one failed category. Memoization is over subsets (64 for vanilla Randy), never game state.
    internal static IReadOnlyDictionary<string, double> Draw(IReadOnlyList<Category> categories)
    {
        var memo = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        Dictionary<string, double> Visit(int[] remaining)
        {
            var key = string.Join(",", remaining);
            if (memo.TryGetValue(key, out var prior)) return prior;
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            var total = remaining.Sum(i => categories[i].Weight);
            if (total > 0)
            {
                foreach (var index in remaining)
                {
                    var category = categories[index];
                    var selected = category.Weight / total;
                    if (!(selected > 0)) continue;
                    foreach (var (incident, chance) in category.Incidents)
                        result[incident] = result.GetValueOrDefault(incident) + selected * chance;
                    if (category.Success < 1)
                        foreach (var (incident, chance) in Visit(remaining.Where(i => i != index).ToArray()))
                            result[incident] = result.GetValueOrDefault(incident) + selected * (1 - category.Success) * chance;
                }
            }
            return memo[key] = result;
        }
        return Visit(Enumerable.Range(0, categories.Count).ToArray());
    }
}
