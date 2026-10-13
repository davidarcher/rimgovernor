namespace RimGovernor.Brain.BuildingRuntime;

// Outdoor food fields fill the layout plan's field blocks at every tier. A plan field zone is a block the station
// chain placed for a crop, sized by FieldCapacityTarget; inside it each crop takes its own adjacent crop block,
// sized by its demand, and grows in place with add-cells.

/// <summary>The next outdoor field step: an empty <see cref="Zone"/> creates a growing zone of <see cref="Crop"/> on <see cref="Cells"/>; otherwise Cells are added to Zone, which grows Crop.</summary>
internal sealed record FieldBlockEdit(string Zone, string Crop, IReadOnlyList<Cell> Cells);

/// <summary>One goal's open field-block demand in the cross-crop ledger: options lead with the goal's own crop and cells.</summary>
internal sealed record FieldShortfall(StandardState Standard, IReadOnlyList<FieldBlockOption> Options, string What);

internal static class LayoutFieldPlanning
{
    /// <summary>The fertility above which soil is rich, the policy survey's line.</summary>
    public const double FieldRichFertility = 1.0;

    /// <summary>The plan's field cells; null with no plan or no field zones.</summary>
    public static HashSet<Cell>? LayoutFieldCells(ColonyProjection facts)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return null;
        }
        var fields = plan.FieldCells();
        return fields.Count > 0 ? fields : null;
    }

    private static readonly (int X, int Z)[] Neighbours = [(0, -1), (-1, 0), (1, 0), (0, 1)];

    private static bool Touches(HashSet<Cell> set, Cell c) =>
        set.Contains(new Cell(c.X, c.Z - 1)) || set.Contains(new Cell(c.X - 1, c.Z)) || set.Contains(new Cell(c.X + 1, c.Z)) || set.Contains(new Cell(c.X, c.Z + 1));

    /// <summary>
    /// Walks the plan's field patches (FieldBlocks order) and returns the first patch's step that still has free
    /// soil: grow the growing zone in the patch that options[0] adopts by its need, or create a new crop block
    /// against the patch's standing zones. A new block takes the first option, in BlockCropOrder, not growing
    /// within FieldBlockNeighbourRadius of that block. <paramref name="options"/> are the viable crops in
    /// preference order. The reason explains a refusal: no field blocks, or every block full.
    /// </summary>
    public static (FieldBlockEdit? Edit, string Reason) PlanFieldBlock(ColonyProjection facts, Cell anchor, IReadOnlyList<FieldBlockOption> options, IReadOnlyList<Cell> protectedCells)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return (null, "no layout plan");
        }
        var blocks = plan.FieldBlocks(anchor);
        if (blocks.Count == 0)
        {
            return (null, "layout plan has no field blocks");
        }
        if (options.Count == 0 || options[0].Needed <= 0)
        {
            return (null, "no field cells needed");
        }
        var blocked = new HashSet<Cell>(protectedCells);
        var census = new Dictionary<Cell, SiteCell>(facts.Cells.Count);
        var rich = new HashSet<Cell>();
        foreach (var c in facts.Cells)
        {
            census[c.Cell] = c;
            if (c.Fertility.TryGetValue(out var f) && f > FieldRichFertility)
            {
                rich.Add(c.Cell);
            }
        }
        var growing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in facts.Farms)
        {
            growing[f.Id] = f.Crop;
        }
        var inedible = new HashSet<string>(StringComparer.Ordinal);
        var trees = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in facts.Definitions)
        {
            if (d.Edible.TryGetValue(out var edible) && !edible)
            {
                inedible.Add(d.Name);
            }
            if (d.BlockAdjacentSow.TryGetValue(out var blocksSow) && blocksSow && d.HarvestDestroysPlant.TryGetValue(out var destroys) && destroys)
            {
                trees.Add(d.Name);
            }
        }
        // The native sower leaves a crop beside a sown tree and a tree beside a sown crop unsown, so a zone keeps
        // one cell clear of every zone of the other kind; tree zones never grow by adoption either (AdoptsZone).
        var treeZone = new HashSet<Cell>();
        var cropZone = new HashSet<Cell>();
        foreach (var c in facts.Cells)
        {
            if (c.ZoneId.TryGetValue(out var id) && growing.TryGetValue(id, out var crop))
            {
                (trees.Contains(crop) ? treeZone : cropZone).Add(c.Cell);
            }
        }
        static bool NearZone(HashSet<Cell> zone, Cell cell)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (zone.Contains(new Cell(cell.X + dx, cell.Z + dz)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        HashSet<Cell> FreeFor(HashSet<Cell> block, CropChoice crop)
        {
            crop.FertilityMin.TryGetValue(out var floor);
            var other = PolicyFunctions.IsTreeCrop(crop) ? cropZone : treeZone;
            var free = new HashSet<Cell>();
            foreach (var cell in block)
            {
                if (census.TryGetValue(cell, out var c) && !blocked.Contains(cell) && FreeFieldSoil(c, floor) && !NearZone(other, cell))
                {
                    free.Add(cell);
                }
            }
            return free;
        }
        foreach (var blockCells in blocks)
        {
            var block = blockCells;
            var zoneCells = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
            foreach (var cell in block)
            {
                if (!census.TryGetValue(cell, out var c))
                {
                    continue;
                }
                if (c.ZoneId.TryGetValue(out var id) && id != "" && growing.ContainsKey(id))
                {
                    if (!zoneCells.TryGetValue(id, out var set))
                    {
                        zoneCells[id] = set = [];
                    }
                    set.Add(cell);
                }
            }
            // A patch holds adjacent crop blocks, one growing zone each: options[0] grows the largest zone it
            // adopts in place, a ring of free cells around it; hay and social crops never share a food zone. Zones
            // never move.
            var zone = "";
            var occupied = new HashSet<Cell>();
            foreach (var (id, cells) in zoneCells)
            {
                occupied.UnionWith(cells);
                if (!AdoptsZone(options[0].Crop, growing[id], inedible))
                {
                    continue;
                }
                if (zone == "" || cells.Count > zoneCells[zone].Count || cells.Count == zoneCells[zone].Count && string.CompareOrdinal(id, zone) < 0)
                {
                    zone = id;
                }
            }
            if (zone != "")
            {
                var adds = ConnectedAdds(zoneCells[zone], FreeFor(block, options[0].Crop), options[0].Needed);
                if (adds.Count > 0)
                {
                    return (new FieldBlockEdit(zone, growing[zone], adds), "");
                }
            }
            // A new crop block sits against the patch's standing blocks, with no gap; its crop avoids one growing
            // within FieldBlockNeighbourRadius of the block itself, not of the whole patch.
            var first = ConnectedPick(FreeFor(block, options[0].Crop), occupied, rich, anchor, options[0].Needed);
            if (first.Count > 0)
            {
                var neighbours = FieldBlockNeighbours(facts.Cells, new HashSet<Cell>(first), growing);
                foreach (var o in PolicyFunctions.BlockCropOrder(options, neighbours))
                {
                    if (o.Needed <= 0)
                    {
                        continue;
                    }
                    var cells = ConnectedPick(FreeFor(block, o.Crop), occupied, rich, anchor, o.Needed);
                    if (cells.Count > 0)
                    {
                        return (new FieldBlockEdit("", o.Crop.Name, cells), "");
                    }
                }
            }
        }
        return (null, "every field block is full");
    }

    /// <summary>Needed cells weighted by urgency: goal priority 0 is the most urgent of the 0-4 classes.</summary>
    public static int FieldShortfallWeight(FieldShortfall s) =>
        s.Options.Count == 0 ? 0 : (5 - s.Standard.Standard.Priority) * s.Options[0].Needed;

    /// <summary>Orders the ledger heaviest first (stable); the first placed block takes the patch's richest free cells. It decides order only, never how many cells a goal gets.</summary>
    public static List<FieldShortfall> RankFieldShortfalls(IEnumerable<FieldShortfall> ledger) =>
        ledger.OrderByDescending(FieldShortfallWeight).ToList();

    /// <summary>The crops a new field block may take: the candidate first, then every other plantable outdoor soil candidate in score order.</summary>
    public static List<FieldBlockOption> FieldBlockOptions(SiteTypeCandidate candidate, IEnumerable<SiteTypeCandidate> all)
    {
        var options = new List<FieldBlockOption> { new() { Crop = candidate.Crop, Needed = candidate.Needed } };
        var seen = new HashSet<string>(StringComparer.Ordinal) { candidate.Crop.Name };
        foreach (var c in all)
        {
            if (c.Kind == SiteKind.Outdoor && c.Buildings.Count == 0 && c.Cells > 0 && seen.Add(c.Crop.Name))
            {
                options.Add(new FieldBlockOption { Crop = c.Crop, Needed = c.Needed });
            }
        }
        return options;
    }

    /// <summary>The set of crops growing in a zone with a cell within FieldBlockNeighbourRadius (Chebyshev) of the block's bounding box.</summary>
    public static HashSet<string> FieldBlockNeighbours(IEnumerable<SiteCell> cells, HashSet<Cell> block, IReadOnlyDictionary<string, string> growing)
    {
        var first = true;
        var lo = new Cell(0, 0);
        var hi = new Cell(0, 0);
        foreach (var c in block)
        {
            if (first)
            {
                lo = c;
                hi = c;
                first = false;
                continue;
            }
            lo = new Cell(Math.Min(lo.X, c.X), Math.Min(lo.Z, c.Z));
            hi = new Cell(Math.Max(hi.X, c.X), Math.Max(hi.Z, c.Z));
        }
        var r = PolicyFunctions.FieldBlockNeighbourRadius;
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cells)
        {
            if (!c.ZoneId.TryGetValue(out var id) || !growing.TryGetValue(id, out var crop)
                || c.Cell.X < lo.X - r || c.Cell.X > hi.X + r || c.Cell.Z < lo.Z - r || c.Cell.Z > hi.Z + r)
            {
                continue;
            }
            output.Add(crop);
        }
        return output;
    }

    /// <summary>Reports whether a field of <paramref name="crop"/> grows a zone of <paramref name="zoneCrop"/>: its own crop always; a food field also adopts any food zone, keeping the zone's crop, but never a hay or social crop's zone.</summary>
    public static bool AdoptsZone(CropChoice crop, string zoneCrop, HashSet<string> inedible)
    {
        if (zoneCrop == crop.Name)
        {
            return true;
        }
        var known = crop.Edible.TryGetValue(out var edible);
        return (!known || edible) && !inedible.Contains(zoneCrop);
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
        c.Walkable.TryGetValue(out var walk) && walk && !c.Occupied()
        && c.Zone.TryGetValue(out var zone) && !zone
        && c.Roofed.TryGetValue(out var roof) && !roof
        && c.Fertility.TryGetValue(out var soil) && soil > 0 && soil >= floor;

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
