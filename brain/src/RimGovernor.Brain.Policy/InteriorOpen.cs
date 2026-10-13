namespace RimGovernor.Brain.Policy;

/// <summary>
/// Classifies a planned room's interior cells against the census cells. A store's zone is created only
/// when every interior cell is settled: walkable (Open), or a Kept cell.
/// </summary>
public sealed record InteriorReading
{
    /// <summary>Open cells are walkable.</summary>
    public IReadOnlyList<Cell> Open { get; init; } = [];

    /// <summary>Dig cells are natural rock still to be mined.</summary>
    public IReadOnlyList<Cell> Dig { get; init; } = [];

    /// <summary>Pending cells are unseen (fogged, unknown) or hold a ruin that clearance still removes: they may turn out open.</summary>
    public IReadOnlyList<Cell> Pending { get; init; } = [];

    /// <summary>
    /// Kept cells are seen, not walkable, and neither rock nor clearable (water, a boulder-free edifice, a
    /// tree): the room keeps them and the zone leaves them out.
    /// </summary>
    public IReadOnlyList<Cell> Kept { get; init; } = [];

    /// <summary>True when no interior cell is left to dig, unseen or to clear, and at least one is open: every cell is walkable or kept.</summary>
    public bool Ready() => Dig.Count == 0 && Pending.Count == 0 && Open.Count > 0;

    /// <summary>
    /// The walkable cells a zone takes. Native refuses a non-contiguous zone, so a kept cell that splits the
    /// open cells leaves the largest cardinally connected part (the one holding the lowest cell on a tie).
    /// </summary>
    public IReadOnlyList<Cell> Cover() => PolicyFunctions.LargestComponent(Open);
}

public static partial class PolicyFunctions
{
    /// <summary>Reads interior against cells, in reading order.</summary>
    public static InteriorReading InteriorOf(Rectangle interior, IReadOnlyList<SiteCell> cells)
    {
        var site = new Dictionary<Cell, SiteCell>(cells.Count);
        foreach (var c in cells)
        {
            site[c.Cell] = c;
        }
        return ReadInterior(interior, c => site.TryGetValue(c, out var s) ? (s, true) : (null, false));
    }

    internal static InteriorReading ReadInterior(Rectangle interior, Func<Cell, (SiteCell? Cell, bool Ok)> lookup) =>
        ReadCells(RectCells(interior), lookup);

    /// <summary>ReadInterior over an exact cell list (a non-rectangular region).</summary>
    internal static InteriorReading ReadCells(IReadOnlyList<Cell> cells, Func<Cell, (SiteCell? Cell, bool Ok)> lookup)
    {
        List<Cell> open = [], dig = [], pending = [], kept = [];
        foreach (var cell in cells)
        {
            var (c, listed) = lookup(cell);
            var known = false;
            var walkable = false;
            var ruin = false;
            if (c is not null)
            {
                known = c.Walkable.TryGetValue(out walkable);
                ruin = c.Ruin();
            }
            if (!listed || !known)
            {
                pending.Add(cell);
            }
            else if (walkable)
            {
                open.Add(cell);
            }
            else if (RockCell(c!))
            {
                dig.Add(cell);
            }
            else if (ruin)
            {
                pending.Add(cell);
            }
            else
            {
                kept.Add(cell);
            }
        }
        return new InteriorReading { Open = open, Dig = dig, Pending = pending, Kept = kept };
    }

    /// <summary>The largest cardinally connected part of cells, sorted.</summary>
    internal static IReadOnlyList<Cell> LargestComponent(IReadOnlyList<Cell> cells)
    {
        var member = new HashSet<Cell>(cells);
        var sorted = StockpileSorted(cells);
        var seen = new HashSet<Cell>();
        List<Cell> best = [];
        foreach (var start in sorted)
        {
            if (!seen.Add(start))
            {
                continue;
            }
            var part = new List<Cell> { start };
            var queue = new Queue<Cell>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var n in StockpileNeighbours(c))
                {
                    if (member.Contains(n) && seen.Add(n))
                    {
                        part.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }
            if (part.Count > best.Count)
            {
                best = part;
            }
        }
        return best.SortedBy(CellLess);
    }

    /// <summary>The planned room's interior reading against the census.</summary>
    public static InteriorReading InteriorOpen(this PlannedRoom r, IReadOnlyList<SiteCell> cells) => InteriorOf(r.Region.Bounds(), cells);

    /// <summary>Reports whether role is a room whose purpose is a store, the dug rooms whose excavation is a priority.</summary>
    public static bool IsStoreRoom(PlannedRole role) => role is PlannedRole.Storage or PlannedRole.Armory or PlannedRole.Wardrobe;
}
