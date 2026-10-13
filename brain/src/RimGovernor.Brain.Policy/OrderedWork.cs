using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Ranks interruption evidence without excluding a pawn from Auto planning. Unknown provenance is
    /// costlier than a known idle/ordinary job. Active controller claims and native legality are checked
    /// independently.
    /// </summary>
    internal static int OrderedWorkCost(Fact<bool> forced, params Fact<uint>[] queued)
    {
        var cost = 0;
        if (!forced.TryGetValue(out var f))
        {
            cost = 2;
        }
        else if (f)
        {
            cost = 1;
        }
        foreach (var q in queued)
        {
            if (!q.TryGetValue(out var v))
            {
                cost += 2;
            }
            else if (v > 0)
            {
                cost++;
            }
        }
        return cost;
    }
}
