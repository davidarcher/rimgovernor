using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One store's place: the planned room whose interior it covers, or a clean rectangle inside that room. The
/// interior rectangle is the site's identity: a zone is matched to it by where it stands and by its role's
/// prefix, never by a key carrying the census room ID that RimWorld renumbers. Whole-footprint stores expand and
/// consolidate their owned fragments as ground clears. Moving a store creates its replacement before deleting
/// the old zone.
/// </summary>
public sealed record StoreSite
{
    /// <summary>The zone's role; only its prefix (before ':') identifies it.</summary>
    public string Role { get; init; } = "";

    /// <summary>The planned room's floor, walls excluded.</summary>
    public Rectangle Interior { get; init; }

    /// <summary>
    /// Width and Height, both positive, ask for a clean rectangle of that size inside Interior nearest Anchor;
    /// zero covers the whole interior.
    /// </summary>
    public int Width { get; init; }

    public int Height { get; init; }

    public Cell Anchor { get; init; }

    /// <summary>Limits a rectangle to roofed ground.</summary>
    public bool Roofed { get; init; }

    /// <summary>Interior cells a rectangle never takes (planned furniture, the chairs).</summary>
    public IReadOnlyList<Cell> Avoid { get; init; } = [];

    public StockpileFilter? Filter { get; init; }

    public StockpilePriority Priority { get; init; }

    /// <summary>
    /// When set, the room's exact floor: the footprint of a whole-footprint store instead of the bounding
    /// Interior, so a reshaped room's zone follows it cell for cell.
    /// </summary>
    public Region Region { get; init; } = new();

    /// <summary>When set, the rectangular placement areas of the site.</summary>
    internal IReadOnlyList<Rectangle>? Regions { get; init; }

    /// <summary>Matches a zone by the whole role key (a bench ID, unlike a census room ID that RimWorld renumbers) instead of its prefix.</summary>
    internal bool Exact { get; init; }

    /// <summary>The ground that identifies the site.</summary>
    internal List<Cell> Footprint()
    {
        if (Regions is not null)
        {
            var cells = new List<Cell>();
            foreach (var r in Regions)
            {
                cells.AddRange(PolicyFunctions.RectCells(r));
            }
            return cells;
        }
        if (!Region.Empty())
        {
            return [.. Region.Cells()];
        }
        return [.. PolicyFunctions.RectCells(Interior)];
    }

    /// <summary>Reports a store that covers its whole ground rather than a patch of it.</summary>
    internal bool WholeFootprint() => Width <= 0 || Height <= 0;

    /// <summary>Reports a footprint that is not the Interior rectangle.</summary>
    internal bool Reshaped() => !Region.Empty() && !Region.IsRect();

    /// <summary>Reports whether a zone belongs to the site: its role matches and it stands on the site's footprint.</summary>
    internal bool Serves(StockpileZone z)
    {
        if (z.Role.Length == 0)
        {
            return false;
        }
        if ((Exact && z.Role != Role) || (!Exact && PolicyFunctions.StockpileRolePrefix(z.Role) != PolicyFunctions.StockpileRolePrefix(Role)))
        {
            return false;
        }
        return PolicyFunctions.StockpileTouches(z.Cells, PolicyFunctions.CellSet(Footprint()));
    }

    /// <summary>
    /// Chooses the site's drags. Native resolves obstacles and connected zones inside each drag; policy
    /// exclusions limit the requested ground.
    /// </summary>
    internal List<Rectangle> Rectangles(StockpileOpen open)
    {
        if (Regions is null)
        {
            // The zone waits until the interior is settled: no stand-in store covers a room still being dug,
            // unseen or cleared.
            (SiteCell Cell, bool Ok) Lookup(Cell c) => open.Cells.TryGetValue(c, out var sc) ? (sc, true) : (new SiteCell(), false);
            var reading = PolicyFunctions.ReadInterior(Interior, Lookup);
            if (Reshaped())
            {
                reading = PolicyFunctions.ReadCells(Region.Cells(), Lookup);
            }
            if (!reading.Ready())
            {
                return [];
            }
        }
        if (Width <= 0 || Height <= 0)
        {
            IReadOnlyList<Rectangle> regions = Regions ?? [Interior];
            // Only policy exclusions change the drag. Native handles obstacles.
            var allowed = PolicyFunctions.CellSet(PolicyFunctions.WithoutCells(Footprint(), Avoid));
            var changed = Avoid.Count != 0 || Reshaped();
            foreach (var c in allowed.ToList())
            {
                if (open.Protected.Contains(c) || open.Taken.Contains(c))
                {
                    allowed.Remove(c);
                    changed = true;
                }
            }
            if (changed)
            {
                regions = PolicyFunctions.CellRects(allowed);
            }
            var output = new List<Rectangle>();
            foreach (var r in regions)
            {
                if (PolicyFunctions.CoverCells(open, PolicyFunctions.RectCells(r)).Count > 0)
                {
                    output.Add(r);
                }
            }
            return output;
        }
        var within = open with { Only = PolicyFunctions.CellSet(PolicyFunctions.WithoutCells(Footprint(), Avoid)) };
        Func<SiteCell, bool>? allow = null;
        if (Roofed)
        {
            allow = c => c.Roofed.TryGetValue(out var roofed) && roofed;
        }
        return PolicyFunctions.RectangleSites(within, Anchor, Width, Height, allow, 1);
    }

    /// <summary>Estimates usable ground for capacity. It does not choose components.</summary>
    internal List<Cell> Cells(StockpileOpen open)
    {
        var cells = new List<Cell>();
        foreach (var r in Rectangles(open))
        {
            cells.AddRange(PolicyFunctions.CoverCells(open, PolicyFunctions.RectCells(r)));
        }
        return PolicyFunctions.StockpileSorted(cells);
    }
}

public static partial class PolicyFunctions
{
    internal static bool StoreServed(IReadOnlyList<StockpileZone> zones, StoreSite site) => zones.Any(site.Serves);

    /// <summary>The open cells of room, in order.</summary>
    internal static List<Cell> CoverCells(StockpileOpen open, IEnumerable<Cell> room) => StockpileSorted(room.Where(open.Ok));

    private readonly record struct ScoredSite(Rectangle Site, long Score);

    /// <summary>
    /// Lists up to limit free width x height rectangles whose every cell is open and passes allow (null: any
    /// open cell), nearest anchor first, ties by corner.
    /// </summary>
    internal static List<Rectangle> RectangleSites(StockpileOpen open, Cell anchor, int width, int height, Func<SiteCell, bool>? allow, int limit)
    {
        var corners = StockpileSorted(open.Cells.Keys);
        var found = new List<ScoredSite>();
        foreach (var p in corners)
        {
            var site = new Rectangle(p.X, p.Z, width, height);
            var ok = true;
            foreach (var c in RectCells(site))
            {
                ok = ok && open.Ok(c) && (allow is null || allow(open.Cells[c]));
            }
            if (ok)
            {
                found.Add(new ScoredSite(site, SquaredDistance(new Cell(p.X + (width / 2), p.Z + (height / 2)), anchor)));
            }
        }
        return [.. found.OrderBy(f => f.Score).Take(Math.Min(found.Count, limit)).Select(f => f.Site)];
    }

    /// <summary>
    /// Deletes the zones standing outside every site that serves their role: several sites may share a prefix
    /// (the warehouses), and a zone on any of their footprints stays. A move creates before it deletes: while no
    /// zone serves the new site the delete names the site's role in After, and the review admits it only once
    /// that create is admitted, so a deferred or refused create never leaves the role without a zone.
    /// </summary>
    internal static List<StockpileEdit> StoreSiteMoves(IReadOnlyList<StockpileZone> zones, IReadOnlyList<StoreSite> sites)
    {
        var output = new List<StockpileEdit>();
        foreach (var z in zones)
        {
            StoreSite? serving = null;
            var after = "";
            var inRoom = false;
            var replaced = false;
            foreach (var site in sites)
            {
                var roleMatch = z.Role.Length != 0
                    && ((site.Exact && z.Role == site.Role) || (!site.Exact && StockpileRolePrefix(z.Role) == StockpileRolePrefix(site.Role)));
                if (!roleMatch)
                {
                    continue;
                }
                serving ??= site;
                if (site.Serves(z))
                {
                    inRoom = true;
                }
                if (StoreServed(zones, site))
                {
                    replaced = true;
                }
                else if (after.Length == 0)
                {
                    after = site.Role;
                }
            }
            if (serving is null || inRoom)
            {
                continue;
            }
            if (replaced)
            {
                after = "";
            }
            output.Add(new StockpileEdit
            {
                Kind = StockpileEditKind.Delete,
                Zone = z.Id,
                Role = z.Role,
                After = after,
                Explanation = string.Create(
                    CultureInfo.InvariantCulture,
                    $"stockpile {z.Id} ({z.Role}): its site moved to {serving.Role}, delete; {z.Used()} used cells rehome"
                ),
            });
        }
        return output;
    }

    /// <summary>Creates a zone for every site no zone serves yet, in order, claiming each zone's ground in open.Taken so a later site never overlaps it.</summary>
    internal static List<StockpileEdit> StoreSiteEdits(IReadOnlyList<StockpileZone> zones, IReadOnlyList<StoreSite> sites, StockpileOpen open)
    {
        var output = new List<StockpileEdit>();
        foreach (var site in sites)
        {
            if (StoreServed(zones, site))
            {
                continue;
            }
            var rects = site.Rectangles(open);
            var cover = site.Cells(open);
            if (site.WholeFootprint() && site.Regions is null && rects.Count > 0
                && (site.Reshaped() || rects.Count > 1 || cover.Count != rects[0].Width * rects[0].Height))
            {
                // A reshaped room, or ground with furniture on it, is covered cell for cell, furniture left
                // out: one zone per connected part.
                foreach (var part in ConnectedComponents(cover))
                {
                    open.Taken.Claim(part);
                    output.Add(new StockpileEdit
                    {
                        Kind = StockpileEditKind.Create,
                        Role = site.Role,
                        CellList = part,
                        Filter = site.Filter,
                        Priority = site.Priority,
                        Explanation = string.Create(
                            CultureInfo.InvariantCulture,
                            $"stockpile role {site.Role}: create over {part.Count} region cells from ({part[0].X},{part[0].Z})"
                        ),
                    });
                }
                continue;
            }
            foreach (var r in rects)
            {
                open.Taken.Claim(RectCells(r));
                output.Add(new StockpileEdit
                {
                    Kind = StockpileEditKind.Create,
                    Role = site.Role,
                    Rectangle = r,
                    Filter = site.Filter,
                    Priority = site.Priority,
                    Explanation = string.Create(
                        CultureInfo.InvariantCulture,
                        $"stockpile role {site.Role}: create rectangle {r.Width}x{r.Height} at ({r.X},{r.Z})"
                    ),
                });
            }
        }
        return output;
    }
}
