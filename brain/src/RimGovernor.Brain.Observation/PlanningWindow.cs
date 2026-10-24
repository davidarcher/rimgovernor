namespace RimGovernor.Brain.Observation;

/// <summary>
/// Reads site cells through observations_get_cells. The scheduler decides whether its held window remains usable
/// or needs refreshing. Returned Held evidence preserves the observed tick and producing method for section
/// provenance.
/// </summary>
public interface IPlanningWindowSource
{
    Task<Held<PlanningCells>> PlanningWindowAsync(C.Identity identity, Rectangle region, CancellationToken cancellationToken);
}

/// <summary>
/// A planning window source that also knows the stored layout plan's extent (<c>LayoutPlan.Extent</c>): the
/// window then covers the plan as well as the colonists. An empty rect means no plan.
/// </summary>
public interface IPlanExtentSource : IPlanningWindowSource
{
    Task<Rectangle> PlanExtentAsync(CancellationToken cancellationToken);
}

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
    /// The planning facts a planning read needs. A read that did not request them carries
    /// <see cref="O.Planning.NotRequested"/>, and a planner reading it raises <c>RequiredFactException("planning")</c>.
    /// </summary>
    public static O.PlanningFacts RequirePlanning(O.ColonyFactsSnapshot facts) =>
        facts.Planning is O.Planning.Requested requested ? requested.Facts : throw new RequiredFactException("planning");

    /// <summary>
    /// Sets the projection's window from the flow's source; a reading with no source keeps an empty window, which
    /// plans no site.
    /// </summary>
    internal static async Task<ColonyProjection> FillPlanningWindow(C.Identity identity, ColonyProjection projection, CancellationToken cancellationToken)
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
        var held = await source.PlanningWindowAsync(identity, PlanningWindowRules.PlanningWindowRect(projection.Bounds, extent), cancellationToken).ConfigureAwait(false);
        return projection with { Region = held.Value.Region, Cells = held.Value.Cells, Window = held };
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
