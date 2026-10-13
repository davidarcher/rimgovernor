namespace RimGovernor.Brain.Policy;

// Go: action_rules.go
public static partial class PolicyFunctions
{
    /// <summary>The things a census reports unsafe to haul, sorted.</summary>
    public static IReadOnlyList<string> UnsafeLoot(IEnumerable<LootItem> rows)
    {
        var output = new List<string>();
        foreach (var row in rows)
        {
            if (row.SafetyKnown && !row.SafeToHaul)
            {
                output.Add(row.Supply.Thing);
            }
        }

        output.Sort(StringComparer.Ordinal);
        return output;
    }
}
