namespace RimGovernor.Brain.Runtime;

public static class PlanningWindowReads
{
    private static readonly AsyncLocal<IPlanningWindowSource?> PlanningWindowScope = new();

    /// <summary>
    /// Attaches <paramref name="source"/> to the current async flow so every planning colony read under it (a
    /// review, a planner's own observation) fills its window from source when the reply carries none. Dispose
    /// the result to detach it.
    /// </summary>
    public static IDisposable WithPlanningWindow(IPlanningWindowSource source)
    {
        var previous = PlanningWindowScope.Value;
        PlanningWindowScope.Value = source;
        return new Scope(() => PlanningWindowScope.Value = previous);
    }

    /// <summary>The source the current async flow carries, or null.</summary>
    public static IPlanningWindowSource? PlanningWindowFrom() => PlanningWindowScope.Value;

    /// <summary>
    /// Sets the projection's window from the flow's source; a reading with no source keeps an empty window, which
    /// plans no site.
    /// </summary>
    internal static async Task<ColonyReading> FillPlanningWindow(RimGovernor.Brain.GameFacts.Common.Identity identity, ColonyReading projection, CancellationToken cancellationToken)
    {
        var source = PlanningWindowFrom();
        if (source is null)
        {
            return projection;
        }
        var extent = default(Rectangle);
        if (source is IPlanExtentSource plans)
        {
            extent = await plans.PlanExtentAsync(cancellationToken).ConfigureAwait(false);
        }
        var held = await source.PlanningWindowAsync(identity, PlanningWindowRules.PlanningWindowRect(projection.Facts.MapBounds, extent), cancellationToken).ConfigureAwait(false);
        return projection with { Window = held };
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}
