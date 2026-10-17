using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>An owned stockpile zone shelves may serve: its census cells now and the filter and priority it was created with.</summary>
public sealed record ShelfZone
{
    public string Zone { get; init; } = "";

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public required StockpileFilter Filter { get; init; }

    public StockpilePriority Priority { get; init; }
}

/// <summary>One shelf the planner placed for a zone. Building is the built shelf's identity, empty while it is not (yet) built; Open marks a placement still in flight.</summary>
public sealed record ShelfRecord
{
    public string Zone { get; init; } = "";

    public string Building { get; init; } = "";

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public bool Open { get; init; }
}

public enum ShelfStepKind
{
    [WireName("")]
    None,
    Build,
}

/// <summary>The next shelf action: place another shelf inside a zone (Pieces are the candidate sites in preference order; the caller previews them in turn).</summary>
public sealed record ShelfStep
{
    public ShelfStepKind Kind { get; init; }

    public ShelfZone? Zone { get; init; }

    public IReadOnlyList<InteriorPiece> Pieces { get; init; } = [];
}

/// <summary>The planner's view: the zones, the shelves already placed, the site census and whether the shelf is buildable (researched).</summary>
public sealed record ShelfRequest
{
    public IReadOnlyList<ShelfZone> Zones { get; init; } = [];

    public IReadOnlyList<ShelfRecord> Shelves { get; init; } = [];

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>The planned route cells (<see cref="PolicyFunctions.PlannedWalkways"/>) no shelf may stand on.</summary>
    public IReadOnlySet<Cell> Walkways { get; init; } = new HashSet<Cell>();

    public bool Available { get; init; }
}

/// <summary>
/// One shelf a declared store could take: the zone it would stand in, the store's role, the 2x1 site and the
/// stacks the store gains (the shelf's stacks less the floor stacks its cells give up: 6 - 2 = +4 for Core's
/// Shelf); unknown when either count is.
/// </summary>
public sealed record ShelfCandidate
{
    public string Zone { get; init; } = "";

    public string Role { get; init; } = "";

    public required InteriorPiece Piece { get; init; }

    public Fact<int> Stacks { get; init; }
}

// Shelves. Core's Shelf (Buildings_Furniture.xml, StorageShelfBase) is a 2x1 Building_Storage that holds
// building.maxItemsInCell = 3 stacks per cell, stops deterioration on top, requires the ComplexFurniture
// research and cannot overlap zones: when its blueprint spawns on a stockpile the zone gives up those cells
// (ZoneManager.Notify_NoZoneOverlapThingSpawned), so a shelf sited inside a zone trades its two floor stacks for
// six shelf stacks. Its default settings (Preferred, every category) are replaced by the served zone's desired
// filter and priority by the admin/stockpiles Need (StockpileEditKind.ShelfPatch).
public static partial class StorageRules
{
    public const string ShelfDefinition = "Shelf";

    /// <summary>The stockpile role key a configured shelf is claimed by.</summary>
    public static string ShelfRole(string building) => "shelf:" + building;

    /// <summary>Builds one shelf at a time: an open placement waits; then the first zone (by id) under its shelf quota gets candidate sites.</summary>
    public static ShelfStep NextShelfStep(ShelfRequest r)
    {
        var zones = r.Zones.OrderBy(z => z.Zone, StringComparer.Ordinal);
        if (r.Shelves.Any(s => s.Open))
        {
            return new ShelfStep();
        }
        if (!r.Available)
        {
            return new ShelfStep();
        }
        foreach (var z in zones)
        {
            var shelfCells = 0;
            foreach (var s in r.Shelves)
            {
                if (s.Zone == z.Zone && s.Building.Length != 0)
                {
                    shelfCells += s.Cells.Count;
                }
            }
            if (shelfCells + 2 > ShelfQuotaCells(z.Cells.Count + shelfCells))
            {
                continue;
            }
            var pieces = ShelfSites(z.Cells, r.Cells, r.Walkways);
            if (pieces.Count > 0)
            {
                return new ShelfStep { Kind = ShelfStepKind.Build, Zone = z, Pieces = pieces };
            }
        }
        return new ShelfStep();
    }

    /// <summary>
    /// How many of a stockpile footprint's cells may carry shelves: a third, in whole 2-cell shelves, and at
    /// least one shelf from a 2x2 up, so floor aisles and loose-item cells remain.
    /// </summary>
    public static int ShelfQuotaCells(int footprint)
    {
        if (footprint < 4)
        {
            return 0;
        }
        return Math.Max(2, footprint / 3 / 2 * 2);
    }

    /// <summary>
    /// Every shelf site of every declared, non-retired store a shelf can hold (<see cref="ShelfHolds"/>), zone
    /// by zone (by id), each with its effect in stacks. Generation only: which to build is chosen elsewhere
    /// (NextWarehouseShelf today). None while the planned walkways are unknown.
    /// </summary>
    public static List<ShelfCandidate> ShelfCandidates(StockpileRequest r)
    {
        var output = new List<ShelfCandidate>();
        if (!r.Walkways.TryGetValue(out var walkways))
        {
            return output;
        }
        var avoid = new HashSet<Cell>(walkways);
        var census = new Dictionary<Cell, SiteCell>(r.Cells.Count);
        foreach (var c in r.Cells)
        {
            census[c.Cell] = c;
        }
        foreach (var z in r.Zones.OrderBy(z => z.Id, StringComparer.Ordinal))
        {
            if (StoreRules.StoreOfZone(r.Stores, z) is not { Retired: false } store || !ShelfHolds(store.Site.Filter))
            {
                continue;
            }
            foreach (var piece in ShelfSites(z.Cells, r.Cells, avoid))
            {
                var (floor, _) = StockpileRules.StorageStacks(PlannedRules.RectCells(piece.Rect), census);
                var stacks = r.ShelfStacks.TryGetValue(out var shelf) && floor.TryGetValue(out var given) ? Known(shelf - given) : Unknown<int>();
                output.Add(new ShelfCandidate { Zone = z.Id, Role = store.Role, Piece = piece, Stacks = stacks });
            }
        }
        return output;
    }

    // The categories a shelf's fixed storage settings exclude (Core's StorageShelfBase disallows Chunks, Plants
    // and Buildings), with Chunks' stone child.
    private static readonly HashSet<string> ShelfExcludedCategories = new(StringComparer.Ordinal) { "Chunks", "StoneChunks", "Plants", "Buildings" };

    /// <summary>
    /// Whether a shelf can hold some of what a store's filter allows: any preset base does (each lists items
    /// beyond chunks, plants and buildings); a Nothing base does when it allows a thing def or a category the
    /// shelf does not exclude. A chunk-only filter, or a store declaring no filter, gets no shelf.
    /// </summary>
    public static bool ShelfHolds(StockpileFilter? f)
    {
        if (f is null)
        {
            return false;
        }
        if (f.Base != FilterBase.Nothing)
        {
            return true;
        }
        foreach (var s in f.Allow)
        {
            if (s.Kind == SelectorKind.ThingDef || (s.Kind == SelectorKind.CategoryDef && !ShelfExcludedCategories.Contains(s.Name)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The layout plan's walkways: every cell a planned route crosses (<see cref="CheckRoutes"/>' per-cell path
    /// counts). A shelf costs 50 to path over and is pass-through only, so none stands there. Unknown when the
    /// plan's routes fail.
    /// </summary>
    public static Fact<EquatableArray<Cell>> PlannedWalkways(LayoutPlan plan)
    {
        var routes = CheckRoutes(plan);
        return routes.Ok ? Known(EquatableArray.From(StockpileRules.StockpileSorted(routes.Traffic.Keys))) : Unknown<EquatableArray<Cell>>();
    }

    /// <summary>
    /// Lists the 2x1 shelf placements inside a zone's cells, the wall-backed ones first. A site's cells must be
    /// free, walkable floor off the planned walkways and away from any doorway; every shelf cell must keep a
    /// walkable zone neighbour to be reached from; and the zone left over must stay non-empty and contiguous,
    /// since a split zone is not the zone the planner owns.
    /// </summary>
    public static List<InteriorPiece> ShelfSites(IReadOnlyList<Cell> zone, IReadOnlyList<SiteCell> cells, IReadOnlySet<Cell> walkways)
    {
        var @in = new HashSet<Cell>(zone);
        var site = new Dictionary<Cell, SiteCell>();
        foreach (var c in cells)
        {
            site[c.Cell] = c;
        }

        bool Free(Cell c)
        {
            if (!site.TryGetValue(c, out var s) || !@in.Contains(c) || walkways.Contains(c))
            {
                return false;
            }
            if (!s.Walkable.TryGetValue(out var walk) || !walk || s.Occupied())
            {
                return false;
            }
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (site.TryGetValue(new Cell(c.X + dx, c.Z + dz), out var n))
                    {
                        if (!n.Doorway.TryGetValue(out var door) || door)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        // walled: a 4-neighbour outside the zone that is not walkable.
        bool Walled(Cell c)
        {
            foreach (var n in Neighbours4(c))
            {
                if (@in.Contains(n))
                {
                    continue;
                }
                if (!site.TryGetValue(n, out var s))
                {
                    continue;
                }
                if (s.Walkable.TryGetValue(out var walk) && !walk)
                {
                    return true;
                }
            }
            return false;
        }

        var found = new List<(InteriorPiece Piece, bool Walled)>();
        var sorted = zone.OrderBy(c => c.X).ThenBy(c => c.Z);
        foreach (var c in sorted)
        {
            foreach (var rot in new[] { Rotation.North, Rotation.East })
            {
                var other = new Cell(c.X + 1, c.Z);
                if (rot == Rotation.East)
                {
                    other = new Cell(c.X, c.Z + 1);
                }
                if (!Free(c) || !Free(other) || !ShelfLeavesZoneUsable(zone, @in, site, c, other))
                {
                    continue;
                }
                var piece = NewInteriorPiece(
                    string.Create(CultureInfo.InvariantCulture, $"shelf.{c.X}.{c.Z}.{Wire.Name(rot)}"),
                    ShelfDefinition,
                    new Cell(2, 1),
                    rot,
                    c
                );
                found.Add((piece, Walled(c) && Walled(other)));
            }
        }
        return [.. found.OrderByDescending(f => f.Walled).Select(f => f.Piece)];
    }

    /// <summary>The zone without a and b is non-empty and contiguous, and a and b each touch a remaining walkable zone cell.</summary>
    private static bool ShelfLeavesZoneUsable(IReadOnlyList<Cell> zone, HashSet<Cell> @in, Dictionary<Cell, SiteCell> site, Cell a, Cell b)
    {
        var rest = new HashSet<Cell>(@in.Where(c => c != a && c != b));
        if (rest.Count == 0)
        {
            return false;
        }
        foreach (var shelf in new[] { a, b })
        {
            var reached = false;
            foreach (var n in Neighbours4(shelf))
            {
                if (!rest.Contains(n))
                {
                    continue;
                }
                if (site.TryGetValue(n, out var sn) && sn.Walkable.TryGetValue(out var walk) && walk)
                {
                    reached = true;
                }
            }
            if (!reached)
            {
                return false;
            }
        }
        var start = default(Cell);
        foreach (var c in zone)
        {
            if (rest.Contains(c))
            {
                start = c;
                break;
            }
        }
        var seen = new HashSet<Cell> { start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var n in Neighbours4(c))
            {
                if (rest.Contains(n) && seen.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }
        return seen.Count == rest.Count;
    }

    internal static Cell[] Neighbours4(Cell c) => [new(c.X + 1, c.Z), new(c.X - 1, c.Z), new(c.X, c.Z + 1), new(c.X, c.Z - 1)];
}
