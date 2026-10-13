using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The conduit net the layout plan laid (LayoutPlan.Conduits): the cells of its links and the room interiors
/// and site footprints the links end in. SelectPowerMethod routes a consumer along it, so a connect
/// proposal's cells are the planned path (the built ones skipped, as ever) and not a path of its own. The
/// default value is no net: the router then searches the ground.
/// </summary>
public sealed record PlannedNet
{
    public IReadOnlySet<Cell> Cells { get; init; } = new HashSet<Cell>();

    public IReadOnlyList<Region> Pads { get; init; } = [];

    /// <summary>
    /// The shortest path from start to a destination over the net's cells and the pads that hold start or a
    /// destination, within the cells route allows (terrain that takes a conduit, protected cells out).
    /// Destination first, as PowerRoute. Null when there is no net or it does not join the two.
    /// </summary>
    internal IReadOnlyList<Cell>? Route(Cell start, IReadOnlySet<Cell> destinations, IReadOnlySet<Cell> allow)
    {
        if (Cells.Count == 0)
        {
            return null;
        }
        var allowed = new HashSet<Cell>();
        foreach (var c in Cells)
        {
            if (allow.Contains(c))
            {
                allowed.Add(c);
            }
        }
        foreach (var pad in Pads)
        {
            var holds = pad.Contains(start) || destinations.Any(pad.Contains);
            if (!holds)
            {
                continue;
            }
            foreach (var c in pad.Cells())
            {
                if (allow.Contains(c))
                {
                    allowed.Add(c);
                }
                else
                {
                    allowed.Remove(c);
                }
            }
        }
        return PolicyFunctions.PowerRoute(start, destinations, allowed);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The net the plan holds.</summary>
    public static PlannedNet PlannedNetOf(LayoutPlan plan)
    {
        var cells = new HashSet<Cell>();
        foreach (var l in plan.Conduits)
        {
            foreach (var c in l.Cells)
            {
                cells.Add(c);
            }
        }
        if (cells.Count == 0)
        {
            return new PlannedNet();
        }
        var pads = new List<Region>();
        pads.AddRange(plan.Rooms.Select(r => r.Region));
        pads.AddRange(plan.Sites.Select(s => s.Footprint));
        pads.AddRange(plan.Reservations.Select(r => r.Area));
        return new PlannedNet { Cells = cells, Pads = pads };
    }

    /// <summary>
    /// The planned path from start to a destination, or the shortest over the ground when the plan holds none
    /// that joins them (a consumer the plan never routed, a net cell the terrain refuses).
    /// </summary>
    internal static IReadOnlyList<Cell>? PlannedRoute(PlannedNet net, Cell start, IReadOnlySet<Cell> destinations, IReadOnlySet<Cell> allow)
    {
        var path = net.Route(start, destinations, allow);
        return path is { Count: > 0 } ? path : PowerRoute(start, destinations, allow);
    }
}
