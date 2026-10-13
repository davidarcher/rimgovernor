namespace RimGovernor.Brain.Policy;

/// <summary>
/// The layout's effect on colonist pathing: walls close a cell, fences and doors add their entry cost, doors
/// (full-fill edifices) also forbid the diagonal steps that cut their corner, and traps add the known-trap
/// cost colonists pay (RaiderCosts drops it). A non-null Within confines routes to its cells.
/// </summary>
internal sealed class CorridorCosts
{
    public HashSet<Cell> Closed { get; init; } = [];

    public HashSet<Cell> Full { get; init; } = [];

    public HashSet<Cell> Traps { get; init; } = [];

    public HashSet<Cell>? Within { get; init; }

    public Dictionary<Cell, int> Cost { get; init; } = [];

    /// <summary>A copy that shares the maps and confines routes to <paramref name="within"/>.</summary>
    public CorridorCosts WithWithin(HashSet<Cell> within) => new()
    {
        Closed = Closed,
        Full = Full,
        Traps = Traps,
        Cost = Cost,
        Within = within,
    };
}

public static partial class PolicyFunctions
{
    // Vanilla pathfinder prices the corridor is laid out against: PathFinderJob's move ticks, the Fence def's
    // pathCost, and Building_Door.TicksToOpenNow (45 / DoorOpenSpeed, 1.2 for wood) which
    // PathUtility.GetDoorCost charges a pawn that can open the door. A spike trap has no pathCost;
    // Building_Trap.PathFindCostFor charges 800 to a pawn that knows of it (the player's own faction) and
    // nothing to raiders, who do not know the player's traps and walk over them.
    internal const int PathMoveCardinal = 13;
    internal const int PathMoveDiagonal = 18;
    internal const int PathFenceCost = 80;
    internal const int PathWoodDoorCost = 38;
    internal const int PathKnownTrapCost = 800;

    /// <summary>The base cost of a destroyable wall to a pawn that may destroy it (Cost_BlockedWallBase).</summary>
    internal const int PathBlockedWallBase = 70;

    /// <summary>Cost per hit point of a destroyable wall.</summary>
    internal const double PathBlockedWallPerHp = 0.2;

    /// <summary>The cheapest planned wall to path through by destroying it: the base cost plus the hit points of a wooden wall (195), the weakest stuff the layout may build.</summary>
    internal const int PathWallBashCost = PathBlockedWallBase + (195 / 5);

    internal static readonly Cell[] PathSteps = [new(0, 1), new(1, 0), new(0, -1), new(-1, 0), new(1, 1), new(1, -1), new(-1, -1), new(-1, 1)];

    /// <summary>Reports whether a route holds any of the cells.</summary>
    internal static bool CrossesAny(HashSet<Cell> route, HashSet<Cell> cells)
    {
        foreach (var c in cells)
        {
            if (route.Contains(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Costs as a raider prices them: the player's traps unknown and free, and each of walls bashable at PathWallBashCost rather than closed.</summary>
    internal static CorridorCosts RaiderCosts(CorridorCosts k, IEnumerable<Cell> walls)
    {
        var raider = new CorridorCosts();
        var bashable = new HashSet<Cell>(walls);
        foreach (var c in k.Closed)
        {
            if (!bashable.Contains(c))
            {
                raider.Closed.Add(c);
            }
        }
        foreach (var c in k.Full)
        {
            raider.Full.Add(c);
        }
        foreach (var (c, v) in k.Cost)
        {
            raider.Cost[c] = v;
        }
        foreach (var c in bashable)
        {
            raider.Cost[c] = PathWallBashCost;
            raider.Full.Add(c);
        }
        return raider;
    }
}

internal sealed partial class DefenseSite
{
    /// <summary>What entering <paramref name="to"/> costs a colonist: the base move plus the cell's fence or door, or -1 when the step is impossible.</summary>
    internal int StepCost(CorridorCosts k, Cell from, Cell to)
    {
        if (k.Closed.Contains(to) || !Passable(to) || (k.Within is not null && !k.Within.Contains(to)))
        {
            return -1;
        }
        var move = PathMoveCardinal;
        if (from.X != to.X && from.Z != to.Z)
        {
            move = PathMoveDiagonal;
            foreach (var corner in new[] { new Cell(to.X, from.Z), new Cell(from.X, to.Z) })
            {
                if (k.Closed.Contains(corner) || !Passable(corner) || k.Full.Contains(corner) || (Cells.TryGetValue(corner, out var row) && Positive(row.Door)))
                {
                    return -1;
                }
            }
        }
        var priced = k.Cost.TryGetValue(to, out var extra);
        if (!priced && Cells.TryGetValue(to, out var target) && Positive(target.Door))
        {
            extra = PathWoodDoorCost;
        }
        if (k.Traps.Contains(to))
        {
            extra += PathKnownTrapCost;
        }
        return move + extra;
    }

    /// <summary>
    /// Dijkstra from the sources. Forward, an edge from a to b costs StepCost(a, b); reversed, the same edge
    /// is relaxed from b back to a, so the result is the cost of the remaining route to a source.
    /// </summary>
    internal Dictionary<Cell, int> Distances(CorridorCosts k, IEnumerable<Cell> sources, bool reverse)
    {
        var dist = new Dictionary<Cell, int>();
        var q = new PriorityQueue<Cell, int>();
        foreach (var c in sources)
        {
            if (Passable(c) && !k.Closed.Contains(c))
            {
                dist[c] = 0;
                q.Enqueue(c, 0);
            }
        }
        while (q.TryDequeue(out var at, out var atDist))
        {
            if (atDist > dist[at])
            {
                continue;
            }
            foreach (var d in PathSteps)
            {
                var n = AddCell(at, d);
                var step = reverse ? StepCost(k, n, at) : StepCost(k, at, n);
                if (step < 0)
                {
                    continue;
                }
                if (!dist.TryGetValue(n, out var known) || atDist + step < known)
                {
                    dist[n] = atDist + step;
                    q.Enqueue(n, atDist + step);
                }
            }
        }
        return dist;
    }

    /// <summary>
    /// Reports whether a colonist starting at start still reaches the map edge once the layout stands, and
    /// whether every route the game's pathfinder could pick as cheapest stays off the trap cells. A tie counts
    /// as a crossing, since A* breaks ties by expansion order.
    /// </summary>
    internal bool ColonistRouteAvoidsTraps(CorridorCosts k, Cell start)
    {
        var (route, ok) = ColonistRoute(k, start);
        if (!ok)
        {
            return false;
        }
        foreach (var t in k.Traps)
        {
            if (route!.Contains(t))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Every cell on some cheapest colonist route from start to an edge-reachable border cell; Ok is false when no such route exists.</summary>
    internal (HashSet<Cell>? Route, bool Ok) ColonistRoute(CorridorCosts k, Cell start)
    {
        var goals = new List<Cell>();
        foreach (var (c, row) in Cells)
        {
            if (Positive(row.EdgeReachable) && OnBorder(c))
            {
                goals.Add(c);
            }
        }
        return CheapestRoutes(k, start, goals);
    }

    /// <summary>Every cell on some cheapest route from start to any of goals; Ok is false when none is reachable.</summary>
    internal (HashSet<Cell>? Route, bool Ok) CheapestRoutes(CorridorCosts k, Cell start, IReadOnlyList<Cell> goals)
    {
        var forward = Distances(k, [start], false);
        var best = int.MaxValue;
        foreach (var g in goals)
        {
            if (forward.TryGetValue(g, out var d) && d < best)
            {
                best = d;
            }
        }
        if (best == int.MaxValue)
        {
            return (null, false);
        }
        var backward = Distances(k, goals, true);
        var route = new HashSet<Cell>();
        foreach (var (c, to) in forward)
        {
            if (backward.TryGetValue(c, out var from) && to + from == best)
            {
                route.Add(c);
            }
        }
        return (route, true);
    }

    /// <summary>
    /// Reports whether every cheapest raider route from Entry into the kill zone (goals) stays in the corridor
    /// when bashing through any of the layout's planned walls is priced in: a tie with a bash counts as one.
    /// </summary>
    internal bool RaiderWalksCorridor(CorridorCosts k, DefenseLayout l, IReadOnlyList<Cell> walls, IReadOnlyList<Cell> goals)
    {
        var raider = RaiderCosts(k, walls);
        var bashable = new HashSet<Cell>(walls);
        var (route, ok) = CheapestRoutes(raider, l.Entry, goals);
        return ok && !CrossesAny(route!, bashable);
    }
}
