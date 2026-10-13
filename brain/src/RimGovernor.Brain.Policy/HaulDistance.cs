namespace RimGovernor.Brain.Policy;

/// <summary>
/// One place hauled stock is carried to: a bench, a hospital bed. Cells are the consumer's own footprint, which
/// need not be walkable (a bench is an edifice); Weight is its expected traffic, the number of trips per unit of
/// stock relative to the other consumers. (Go <c>HaulConsumer</c>.)
/// </summary>
public sealed record HaulConsumer
{
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public long Weight { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>HaulCosts</c>: the traffic-weighted walking distance from every census cell to the consumers: for
    /// each consumer a breadth-first search over walkable cells (8-neighbour, RimWorld's pather) starting from the
    /// cells beside its footprint, summed as Weight x steps. A cell some consumer cannot reach from it is absent:
    /// stock stored there is unreachable to that consumer. Unknown walkability is never walked.
    /// </summary>
    public static IReadOnlyDictionary<Cell, long> HaulCosts(IReadOnlyList<SiteCell> cells, IReadOnlyList<HaulConsumer> consumers)
    {
        var walkable = new Dictionary<Cell, bool>(cells.Count);
        foreach (var c in cells)
        {
            walkable[c.Cell] = Positive(c.Walkable);
        }
        bool Walkable(Cell c) => walkable.GetValueOrDefault(c);
        var total = new Dictionary<Cell, long>();
        var reached = new Dictionary<Cell, int>();
        var used = 0;
        foreach (var consumer in consumers)
        {
            if (consumer.Weight <= 0 || consumer.Cells.Count == 0)
            {
                continue;
            }
            used++;
            var distance = new Dictionary<Cell, long>();
            var queue = new Queue<Cell>();
            foreach (var c in consumer.Cells)
            {
                distance[c] = 0;
                queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var next = new Cell(at.X + dx, at.Z + dz);
                        if (distance.ContainsKey(next) || !Walkable(next))
                        {
                            continue;
                        }
                        distance[next] = distance[at] + 1;
                        queue.Enqueue(next);
                    }
                }
            }
            foreach (var (c, d) in distance)
            {
                if (!Walkable(c))
                {
                    continue;
                }
                total[c] = total.GetValueOrDefault(c) + (consumer.Weight * d);
                reached[c] = reached.GetValueOrDefault(c) + 1;
            }
        }
        foreach (var c in total.Keys.ToList())
        {
            if (reached.GetValueOrDefault(c) != used)
            {
                total.Remove(c);
            }
        }
        return total;
    }

    /// <summary>
    /// Go <c>RankSitesByHaul</c>: reorders candidate sites cheapest haul first: a site costs its cheapest cell,
    /// since haulers drop stock on the nearest free cell. A site no consumer path reaches keeps its incoming
    /// order after every reachable one; with no costs (no consumers) the order is unchanged.
    /// </summary>
    public static IReadOnlyList<Rectangle> RankSitesByHaul(IReadOnlyList<Rectangle> sites, IReadOnlyDictionary<Cell, long> costs)
    {
        var rows = new List<(Rectangle Site, long Cost, bool Ok, int Order)>(sites.Count);
        for (var i = 0; i < sites.Count; i++)
        {
            long cost = 0;
            var ok = false;
            foreach (var c in RectCells(sites[i]))
            {
                if (costs.TryGetValue(c, out var v) && (!ok || v < cost))
                {
                    cost = v;
                    ok = true;
                }
            }
            rows.Add((sites[i], cost, ok, i));
        }
        return [.. rows.OrderBy(r => r.Ok ? 0 : 1).ThenBy(r => r.Ok ? r.Cost : 0).ThenBy(r => r.Order).Select(r => r.Site)];
    }
}
