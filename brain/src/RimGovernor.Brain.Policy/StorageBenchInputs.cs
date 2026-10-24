namespace RimGovernor.Brain.Policy;

// Workstation stockpiles: one generic bench-input rule. A bench that works a standing bill consumes stored
// inputs (stone chunks at the stonecutter, ingredients at every other bench), so a small stockpile holding
// exactly those inputs stands in the bench's room, nearest the bench by walking distance, at a priority above
// the general store so hauling brings the inputs to the bench.

/// <summary>One bench consuming stored inputs: where it stands and the sorted thing definitions its active bills' recipes accept.</summary>
public sealed record BenchInput
{
    public string Bench { get; init; } = "";

    public Cell Cell { get; init; }

    public IReadOnlyList<string> Inputs { get; init; } = [];
}

public static partial class StorageRules
{
    /// <summary>
    /// Lists, in bench order, every bench with an active bill and its inputs: the union of every ingredient
    /// alternative of the recipes those bills run. at holds each bench's cell; excluded names the benches whose
    /// inputs another planner stores (the kitchen's, the butcher's). A bench that stands at no known cell, or that
    /// has no input to store, is not listed: it is planned on a pass that knows.
    /// </summary>
    public static List<BenchInput> DeriveBenchInputs(
        IReadOnlyList<GearBench> benches,
        IReadOnlyDictionary<string, Cell> at,
        IReadOnlyDictionary<string, bool> excluded
    )
    {
        var output = new List<BenchInput>();
        foreach (var bench in benches.OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            var standing = at.TryGetValue(bench.Id, out var cell);
            if ((excluded.TryGetValue(bench.Id, out var skip) && skip) || !standing)
            {
                continue;
            }
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bill in bench.Bills)
            {
                if (bill.Active)
                {
                    active.Add(bill.Recipe);
                }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inputs = new List<string>();
            foreach (var recipe in bench.Recipes)
            {
                if (!active.Contains(recipe.Definition))
                {
                    continue;
                }
                foreach (var alternative in recipe.Ingredients)
                {
                    foreach (var amount in alternative)
                    {
                        var name = amount.Resource.Value;
                        if (name.Length != 0 && seen.Add(name))
                        {
                            inputs.Add(name);
                        }
                    }
                }
            }
            if (inputs.Count == 0)
            {
                continue;
            }
            inputs.Sort(StringComparer.Ordinal);
            output.Add(new BenchInput { Bench = bench.Id, Cell = cell, Inputs = inputs });
        }
        return output;
    }
}
