namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Caps the cells one route search visits: enough to reach a geyser GeothermalReachCells away across open
    /// ground, or to cross a map-wide perimeter interior to a moisture pump.
    /// </summary>
    internal const int PowerRouteBound = 1 << 17;

    /// <summary>The shortest 4-neighbour path from start over allowed cells to the first destination reached. See <see cref="RouteCells"/>.</summary>
    internal static List<Cell> PowerRoute(Cell start, IReadOnlySet<Cell> destinations, IReadOnlySet<Cell> allowed) =>
        RouteCells([start], destinations, allowed);

    /// <summary>
    /// The shared deterministic conduit router: a breadth-first search from every allowed start at once over
    /// 4-neighbour steps in a fixed order, so the nearest destination to any start wins and ties go to the
    /// earlier start and the east, west, south, north neighbour order. The path runs destination first and
    /// start last: the generator-to-consumer order lets successive eight-cell methods extend one route.
    /// Unknown terrain and protected cells never become routes (they are not in allowed). Empty means no
    /// route, or more than PowerRouteBound cells visited.
    /// </summary>
    internal static List<Cell> RouteCells(IReadOnlyList<Cell> starts, IReadOnlySet<Cell> destinations, IReadOnlySet<Cell> allowed)
    {
        var previous = new Dictionary<Cell, Cell>();
        var queue = new List<Cell>();
        foreach (var s in starts)
        {
            if (allowed.Contains(s) && !previous.ContainsKey(s))
            {
                previous[s] = s;
                queue.Add(s);
            }
        }
        for (var i = 0; i < queue.Count; i++)
        {
            var c = queue[i];
            if (destinations.Contains(c))
            {
                var path = new List<Cell> { c };
                while (previous[c] != c)
                {
                    c = previous[c];
                    path.Add(c);
                }
                return path;
            }
            foreach (var next in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                if (allowed.Contains(next) && !previous.ContainsKey(next))
                {
                    if (previous.Count >= PowerRouteBound)
                    {
                        return [];
                    }
                    previous[next] = c;
                    queue.Add(next);
                }
            }
        }
        return [];
    }

    /// <summary>
    /// The cells a power conduit may run over on this survey: any surveyed cell whose terrain takes a light
    /// structure. Buildings, walls and doors do not block (PowerConduit sits on the Conduits layer; only
    /// another power transmitter conflicts), nor do trees or ruins. Deep water and other terrain without
    /// footing, hazards (lava) and natural rock block; rock inside the walls of a dug room does not, since the
    /// room is mined out. Cells the survey does not hold are unknown and never routed.
    /// </summary>
    public static HashSet<Cell> ConduitCells(MapSurvey survey, IReadOnlyList<PlannedRoom> rooms)
    {
        var dug = new HashSet<Cell>();
        foreach (var r in rooms)
        {
            if (!r.Dug)
            {
                continue;
            }
            foreach (var c in RectRegion(RoomWalls(r)).Cells())
            {
                dug.Add(c);
            }
        }
        var result = new HashSet<Cell>();
        foreach (var c in survey.Cells)
        {
            if (c.Footing == Footing.None || c.Hazard || (c.Rock && !dug.Contains(c.Cell)))
            {
                continue;
            }
            result.Add(c.Cell);
        }
        return result;
    }
}
