namespace RimGovernor.Brain.Policy;

// Outdoor fields fill the layout plan's field blocks at every tier. A plan field zone is a block the station chain
// placed for a crop, sized by FieldCapacityTarget; inside it each crop takes its own adjacent crop block, sized by its
// demand, and grows in place with add-cells. FieldPlacementRules compares where; these are the block geometry helpers.

/// <summary>The next outdoor field step: an empty <see cref="Zone"/> creates a growing zone of <see cref="Crop"/> on <see cref="Cells"/>; otherwise Cells are added to Zone, which grows Crop.</summary>
public sealed record FieldBlockEdit(string Zone, string Crop, IReadOnlyList<Cell> Cells);

public static class LayoutFieldPlanning
{
    /// <summary>The fertility above which soil is rich, the policy survey's line.</summary>
    public const double FieldRichFertility = 1.0;

    /// <summary>The plan's field cells; null with no plan or no field zones.</summary>
    public static HashSet<Cell>? LayoutFieldCells(ReviewFacts facts)
    {
        if (facts.LayoutPlan is not { } plan)
        {
            return null;
        }
        var fields = plan.FieldCells();
        return fields.Count > 0 ? fields : null;
    }

    private static readonly (int X, int Z)[] Neighbours = [(0, -1), (-1, 0), (1, 0), (0, 1)];

    private static bool Touches(HashSet<Cell> set, Cell c) =>
        set.Contains(new Cell(c.X, c.Z - 1)) || set.Contains(new Cell(c.X - 1, c.Z)) || set.Contains(new Cell(c.X + 1, c.Z)) || set.Contains(new Cell(c.X, c.Z + 1));

    /// <summary>The crops a new field block may take: the candidate first, then every other plantable outdoor soil candidate in score order.</summary>
    public static List<FieldBlockOption> FieldBlockOptions(SiteTypeCandidate candidate, IEnumerable<SiteTypeCandidate> all)
    {
        var options = new List<FieldBlockOption> { new(candidate.Crop, candidate.Needed) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { candidate.Crop.Name };
        foreach (var c in all)
        {
            if (c.Kind == SiteKind.Outdoor && c.Buildings.Count == 0 && c.Cells > 0 && seen.Add(c.Crop.Name))
            {
                options.Add(new FieldBlockOption(c.Crop, c.Needed));
            }
        }
        return options;
    }

    /// <summary>Reports whether a field of <paramref name="crop"/> grows a zone of <paramref name="zoneCrop"/>: its own crop always; a food field also adopts any food zone, keeping the zone's crop, but never a hay or social crop's zone.</summary>
    public static bool AdoptsZone(CropChoice crop, string zoneCrop, HashSet<string> inedible)
    {
        if (zoneCrop == crop.Name)
        {
            return true;
        }
        var edible = crop.Edible;
        return (edible) && !inedible.Contains(zoneCrop);
    }

    /// <summary>
    /// Floods from the zone through <paramref name="set"/> and returns up to <paramref name="want"/> non-zone
    /// cells reached, nearest the zone first, in row-major order: a zone grows in place by rings, so it stays
    /// compact and connected.
    /// </summary>
    public static List<Cell> ConnectedAdds(HashSet<Cell> zone, HashSet<Cell> set, int want)
    {
        var seen = new HashSet<Cell>(zone);
        var frontier = zone.ToList();
        SortCells(frontier);
        var output = new List<Cell>();
        while (frontier.Count > 0 && output.Count < want)
        {
            var next = new List<Cell>();
            foreach (var c in frontier)
            {
                foreach (var (dx, dz) in Neighbours)
                {
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if (seen.Contains(n) || !set.Contains(n))
                    {
                        continue;
                    }
                    seen.Add(n);
                    next.Add(n);
                }
            }
            SortCells(next);
            foreach (var n in next)
            {
                if (output.Count < want)
                {
                    output.Add(n);
                }
            }
            frontier = next;
        }
        SortCells(output);
        return output;
    }

    /// <summary>
    /// Picks up to <paramref name="want"/> cells of <paramref name="free"/> for a new growing zone as one
    /// 4-connected footprint: native refuses a disconnected zone, and a patch's free soil is often split by rock,
    /// trees or buildings. It picks inside free's largest component (ties to the one nearest the anchor). The
    /// block is sized to want and roughly square: it seeds at the free cell touching a standing block (occupied)
    /// nearest the anchor, or the free cell nearest the anchor when none touches, and takes the fullest of the
    /// four sqrt(want)-wide rectangles cornered at the seed, topped up by rings. Rich soil breaks those ties: the
    /// seed prefers a rich cell, and the rectangle holding the most rich cells wins, so the block the field ledger
    /// places first takes the patch's richest free cells.
    /// </summary>
    public static List<Cell> ConnectedPick(HashSet<Cell> free, HashSet<Cell> occupied, HashSet<Cell> rich, Cell anchor, int want)
    {
        if (want <= 0)
        {
            return [];
        }
        HashSet<Cell> best = [];
        var bestNear = -1L;
        var seen = new HashSet<Cell>();
        foreach (var c in SortedCells(free))
        {
            if (seen.Contains(c))
            {
                continue;
            }
            var comp = CellComponent(free, c);
            var near = -1L;
            foreach (var m in comp)
            {
                seen.Add(m);
                var d = CellDist(m, anchor);
                if (near < 0 || d < near)
                {
                    near = d;
                }
            }
            if (comp.Count > best.Count || comp.Count == best.Count && near < bestNear)
            {
                best = comp;
                bestNear = near;
            }
        }
        if (best.Count == 0)
        {
            return [];
        }
        // Rich soil first: the block goes where the soil is rich, in whatever shape it lies; plain soil only tops
        // up what the rich ground cannot hold.
        var richOnly = RichComponent(best, rich);
        if (richOnly.Count > 0 && richOnly.Count < best.Count && 2 * richOnly.Count >= want)
        {
            if (richOnly.Count >= want)
            {
                return ConnectedPick(richOnly, occupied, rich, anchor, want);
            }
            foreach (var c in ConnectedAdds(richOnly, best, want - richOnly.Count))
            {
                richOnly.Add(c);
            }
            return SortedCells(richOnly);
        }
        var clean = CleanRectangle(best, occupied, rich, anchor, want);
        if (clean.Count > 0)
        {
            return clean;
        }
        var seed = new Cell(0, 0);
        var seedNear = -1L;
        var touching = false;
        var seedRich = false;
        foreach (var c in SortedCells(best))
        {
            var t = Touches(occupied, c);
            var d = CellDist(c, anchor);
            var r = rich.Contains(c);
            if (seedNear < 0 || t && !touching || t == touching && (r && !seedRich || r == seedRich && d < seedNear))
            {
                seed = c;
                seedNear = d;
                touching = t;
                seedRich = r;
            }
        }
        var w = 1;
        while (w * w < want)
        {
            w++;
        }
        var h = (want + w - 1) / w;
        HashSet<Cell> picked = [];
        var pickedRich = 0;
        foreach (var (sx, sz) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
        {
            var rect = new HashSet<Cell>();
            var n = 0;
            for (var dz = 0; dz < h && rect.Count < want; dz++)
            {
                for (var dx = 0; dx < w && rect.Count < want; dx++)
                {
                    var c = new Cell(seed.X + sx * dx, seed.Z + sz * dz);
                    if (best.Contains(c))
                    {
                        rect.Add(c);
                        if (rich.Contains(c))
                        {
                            n++;
                        }
                    }
                }
            }
            if (n > pickedRich || n == pickedRich && rect.Count > picked.Count)
            {
                picked = rect;
                pickedRich = n;
            }
        }
        picked = CellComponent(picked, seed);
        foreach (var c in ConnectedAdds(picked, best, want - picked.Count))
        {
            picked.Add(c);
        }
        return SortedCells(picked);
    }

    /// <summary>The 4-connected cells of <paramref name="set"/> reachable from <paramref name="start"/>.</summary>
    public static HashSet<Cell> CellComponent(HashSet<Cell> set, Cell start)
    {
        var output = new HashSet<Cell> { start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dz) in Neighbours)
            {
                var n = new Cell(c.X + dx, c.Z + dz);
                if (set.Contains(n) && output.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }
        return output;
    }

    public static List<Cell> SortedCells(IEnumerable<Cell> set)
    {
        var output = set.ToList();
        SortCells(output);
        return output;
    }

    public static long CellDist(Cell a, Cell b)
    {
        long dx = a.X - b.X;
        long dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    public static void SortCells(List<Cell> cells) =>
        cells.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    /// <summary>Observed walkable, unoccupied, unzoned, unroofed soil at or above the crop's fertility floor.</summary>
    public static bool FreeFieldSoil(SiteCell c, double floor) =>
        c.Walkable && !c.Occupied() && !c.Zone && !c.Roofed && c.Fertility > 0 && c.Fertility >= floor;

    /// <summary>
    /// Want cells of <paramref name="free"/> laid as a w x h rectangle with a partial last row, every cell free:
    /// no notches, so a crop's sowers and harvesters work one compact block. Among the placements it takes the
    /// one touching a standing block, then holding the most rich soil, then nearest the anchor; empty when no
    /// such rectangle fits and the caller falls back to the grown shape. Both orientations are tried.
    /// </summary>
    public static List<Cell> CleanRectangle(HashSet<Cell> free, HashSet<Cell> occupied, HashSet<Cell> rich, Cell anchor, int want)
    {
        var w = 1;
        while (w * w < want)
        {
            w++;
        }
        var h = (want + w - 1) / w;
        List<Cell>? best = null;
        var bestTouch = false;
        var bestRich = -1;
        var bestDist = -1L;
        foreach (var (cols, rows) in new[] { (w, h), (h, w) })
        {
            foreach (var origin in SortedCells(free))
            {
                var cells = new List<Cell>(want);
                var ok = true;
                for (var i = 0; i < want && ok; i++)
                {
                    var c = new Cell(origin.X + i % cols, origin.Z + i / cols);
                    ok = free.Contains(c);
                    cells.Add(c);
                }
                if (!ok || (want + cols - 1) / cols > rows)
                {
                    continue;
                }
                var touch = false;
                var richCells = 0;
                long cx = 0;
                long cz = 0;
                foreach (var c in cells)
                {
                    touch = touch || Touches(occupied, c);
                    if (rich.Contains(c))
                    {
                        richCells++;
                    }
                    cx += c.X;
                    cz += c.Z;
                }
                var d = CellDist(new Cell((int)(cx / want), (int)(cz / want)), anchor);
                if (best is null || touch != bestTouch && touch || touch == bestTouch && (richCells > bestRich || richCells == bestRich && d < bestDist))
                {
                    best = cells;
                    bestTouch = touch;
                    bestRich = richCells;
                    bestDist = d;
                }
            }
        }
        return best is null ? [] : SortedCells(new HashSet<Cell>(best));
    }

    /// <summary>The largest 4-connected run of rich cells within <paramref name="free"/> (ties to the first in row-major order).</summary>
    public static HashSet<Cell> RichComponent(HashSet<Cell> free, HashSet<Cell> rich)
    {
        var richFree = new HashSet<Cell>(free.Where(rich.Contains));
        HashSet<Cell> best = [];
        var seen = new HashSet<Cell>();
        foreach (var c in SortedCells(richFree))
        {
            if (seen.Contains(c))
            {
                continue;
            }
            var comp = CellComponent(richFree, c);
            seen.UnionWith(comp);
            if (comp.Count > best.Count)
            {
                best = comp;
            }
        }
        return best;
    }
}
