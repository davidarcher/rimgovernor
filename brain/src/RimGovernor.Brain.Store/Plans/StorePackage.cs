using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>The store package's free functions (Go <c>store.PlanOpen</c> and friends).</summary>
public static class StorePackage
{
    /// <summary>Bounds one batched bill plan to a round's reconcile.</summary>
    public const int MaxBillPlanActions = 64;

    /// <summary>
    /// <see cref="PolicyFunctions.PlanWorkOpen"/> for a stored plan: an applied building intent is terminal in the
    /// journal, but its plan stays open while the census still shows its blueprint or frame (or cannot say). Only
    /// census-aware retirement ends it; a retired plan's applied buildings are done or gone.
    /// </summary>
    public static bool PlanWorkOpen(PlanState p, Fact<CurrentConstruction> census) =>
        p.Retired ? Standard.WorkOpen(p.Progress) : PolicyFunctions.PlanWorkOpen(p.Progress, census);

    /// <summary>
    /// A plan's open work without a census: an applied building on a plan not yet retired is still open, since
    /// retirement is what reads the census.
    /// </summary>
    public static bool PlanOpen(PlanState p) => PlanWorkOpen(p, default);

    /// <summary><see cref="PlanOpen"/> for one action of plan <paramref name="p"/>.</summary>
    public static bool ProgressOpen(PlanState p, Progress progress) =>
        Standard.WorkOpen([progress]) || (!p.Retired && PolicyFunctions.AppliedBuildingOpen(progress, default));

    /// <summary>A planned room's ring method: a build wave of a "&lt;role&gt;-shell-&lt;x&gt;-&lt;z&gt;" reconcile.</summary>
    public static bool IsRoomShellMethod(MethodId method) => GlobMatch(method.Value, "*-shell-*-build-*");

    /// <summary>Go <c>path.Match</c> restricted to the <c>*</c> wildcard, which never crosses a path separator.</summary>
    private static bool GlobMatch(string name, string pattern)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1)
        {
            return name == pattern;
        }
        if (!name.StartsWith(parts[0], StringComparison.Ordinal) || !name.EndsWith(parts[^1], StringComparison.Ordinal) || name.Length < parts[0].Length + parts[^1].Length)
        {
            return false;
        }
        var at = parts[0].Length;
        var end = name.Length - parts[^1].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var found = name.IndexOf(parts[i], at, end - at, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }
            at = found + parts[i].Length;
        }
        return !name.AsSpan(parts[0].Length, end - parts[0].Length).Contains('/');
    }
}
