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

public static partial class ObservationPackage
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
    /// Sets the projection's window from the flow's source when a planning reply observed planning facts; a
    /// reading with no source keeps an empty window, which plans no site.
    /// </summary>
    internal static async Task<ColonyProjection> FillPlanningWindow(O.ColonyFactsReply reply, C.Identity identity, ColonyProjection projection, CancellationToken cancellationToken)
    {
        if (reply.Outcome?.Observed?.Planning?.Outcome?.Observed is null)
        {
            return projection;
        }
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
        var held = await source.PlanningWindowAsync(identity, PlanningWindowRect(projection.Bounds, extent), cancellationToken).ConfigureAwait(false);
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
