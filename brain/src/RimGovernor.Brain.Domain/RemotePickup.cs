namespace RimGovernor.Brain.Domain;

/// <summary>
/// Remote construction pickup (#2517, #3046): the one rule both delivery paths load by, the brain's Supply sizing
/// (ConstructionMethodCandidates.RemoteLoad) and native's postfix on vanilla's own delivery job (RemotePickupGuard). A
/// delivery is remote when its first stack is farther from the site (D, straight line) than from the nearest storage that
/// accepts it. A remote delivery loads a full carry instead of the site's need and collects other stacks of the material,
/// nearest the first stack first (at most <see cref="MaxCollect"/>), each while worth its detour: d &lt;= D / 2 and
/// d &lt; n * D / capacity, with d the straight-line detour from the first stack and n the stack's items that still fit.
/// </summary>
public static class RemotePickup
{
    /// <summary>The most stacks one remote load collects.</summary>
    public const int MaxCollect = 16;

    /// <summary>Whether a stack <paramref name="siteDistance"/> from its site and <paramref name="storageDistance"/> from storage is remote.</summary>
    public static bool IsRemote(double siteDistance, double storageDistance) => siteDistance > storageDistance;

    /// <summary>
    /// The stacks a remote load collects, in detour order (ties keep the candidates' order): each candidate is a stack, its
    /// detour from the first stack and its items. <paramref name="held"/> is what the load already holds; <paramref name="usable"/>
    /// is asked only of a stack worth its detour.
    /// </summary>
    public static IReadOnlyList<T> Collect<T>(double siteDistance, double capacity, double held,
        IEnumerable<(T Stack, double Detour, double Available)> candidates, Func<T, bool>? usable = null)
    {
        var chosen = new List<T>();
        var remaining = capacity - held;
        foreach (var (stack, detour, available) in candidates.Where(c => c.Detour <= siteDistance / 2).OrderBy(c => c.Detour))
        {
            if (remaining <= 0 || chosen.Count == MaxCollect) break;
            var fits = Math.Min(available, remaining);
            if (!(fits > 0 && detour < fits * siteDistance / capacity) || usable?.Invoke(stack) == false) continue;
            chosen.Add(stack);
            remaining -= fits;
        }
        return chosen;
    }

    /// <summary>A remote job's count: a full carry of what its stacks hold, never under the site's need.</summary>
    public static int Load(int need, int capacity, int held) => Math.Max(need, Math.Min(capacity, held));
}
