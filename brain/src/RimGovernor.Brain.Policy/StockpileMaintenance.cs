using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One owned stockpile as the census holds it now: its cells (the planning cells naming it), the cells holding
/// things (Stored: the census' storage-empty flag false) and the settings the governor last applied.
/// </summary>
public sealed record StockpileZone
{
    public string Id { get; init; } = "";

    public string Role { get; init; } = "";

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Stored { get; init; } = [];

    public required StockpileFilter Filter { get; init; }

    public StockpilePriority Priority { get; init; }

    /// <summary>Counts the cells holding things.</summary>
    public int Used() => Stored.Count;

    /// <summary>The used-cell fraction; an empty zone is 0.</summary>
    public double Fill() => Cells.Count == 0 ? 0 : (double)Used() / Cells.Count;
}

public enum StockpileEditKind
{
    [WireName("")]
    None,

    /// <summary>Places the zone of a declared store no zone serves.</summary>
    Create,
    Delete,
    Retarget,
    Grow,

    /// <summary>Removes the cells a zone's room gave up, or furniture stands on; stock on them drains by vanilla hauling.</summary>
    Shrink,

    /// <summary>Configures a shelf like its zone: Zone names the shelf building, Role is shelf:&lt;buildingID&gt;. (Go <c>StockpileShelfPatch</c>.)</summary>
    [WireName("shelf")]
    ShelfPatch,
}

/// <summary>Creates a rectangle or edits one standing zone. Native placement determines actual created footprints.</summary>
public sealed record StockpileEdit
{
    public StockpileEditKind Kind { get; init; }

    public string Zone { get; init; } = "";

    public string Role { get; init; } = "";

    /// <summary>The site role whose create must be admitted before this delete of a moved zone; empty once the site is served.</summary>
    public string After { get; init; } = "";

    public Rectangle Rectangle { get; init; }

    public IReadOnlyList<Cell> AddedCells { get; init; } = [];

    /// <summary>A shrink's cells.</summary>
    public IReadOnlyList<Cell> RemovedCells { get; init; } = [];

    /// <summary>When set, a create's exact connected ground in place of Rectangle (a room region).</summary>
    public IReadOnlyList<Cell> CellList { get; init; } = [];

    public StockpileFilter? Filter { get; init; }

    public StockpilePriority Priority { get; init; }

    public string Explanation { get; init; } = "";

    /// <summary>The requested ground, not the resolved native footprint.</summary>
    public IReadOnlyList<Cell> Cells()
    {
        if (Kind == StockpileEditKind.Grow)
        {
            return [.. AddedCells];
        }
        if (Kind == StockpileEditKind.Shrink)
        {
            return [.. RemovedCells];
        }
        if (CellList.Count > 0)
        {
            return [.. CellList];
        }
        return PolicyFunctions.RectCells(Rectangle);
    }
}

/// <summary>The review's input.</summary>
public sealed record StockpileRequest
{
    public Tick Tick { get; init; }

    public IReadOnlyList<StockpileZone> Zones { get; init; } = [];

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    public Bounds Bounds { get; init; }

    public IReadOnlyList<Cell> Protected { get; init; } = [];

    /// <summary>The built shelves inside the zones: each carries its zone's desired settings, patched until it does.</summary>
    public IReadOnlyList<StockpileShelf> Shelves { get; init; } = [];

    /// <summary>
    /// The declared stores of the departments that own stockpiles (DeclareStores): each store's filter and
    /// priority are defined once, on the store.
    /// </summary>
    public IReadOnlyList<Store> Stores { get; init; } = [];

    /// <summary>The departments' room demand for layout; the review itself does not read it.</summary>
    public RoomDemand RoomDemand { get; init; } = new();

    /// <summary>The departments' report of stores they could not make usable (StoreDeclaration.Err); the review itself does not read it.</summary>
    public Exception? SiteErr { get; init; }

    /// <summary>The planned storage rooms (storage, armory and wardrobe, the materials yard's fence ring) not yet standing: the planner raises their shells (StockpileReview.Rooms).</summary>
    public IReadOnlyList<PlannedRole> Rooms { get; init; } = [];

    /// <summary>The shelves placed for the zones, built or in flight.</summary>
    public IReadOnlyList<ShelfRecord> ShelfRecords { get; init; } = [];

    /// <summary>Names the zones whose shelf attempts are used up.</summary>
    public IReadOnlyDictionary<string, bool> ShelfSpent { get; init; } = new Dictionary<string, bool>();

    /// <summary>Whether a Shelf is buildable (researched) and the planner can place it.</summary>
    public bool ShelfAvailable { get; init; }
}

/// <summary>One built shelf serving an owned zone and the settings last patched onto it (Patched false: never patched, native defaults).</summary>
public sealed record StockpileShelf
{
    public string Building { get; init; } = "";

    public string Zone { get; init; } = "";

    public int Cells { get; init; }

    public bool Patched { get; init; }

    public StockpileFilter? Filter { get; init; }

    public StockpilePriority Priority { get; init; }
}

/// <summary>The outcome: the zone edits this cycle proposes, the planned rooms whose shells are owed, and why none stands.</summary>
public sealed record StockpileReview
{
    public bool Known { get; init; }

    public bool Active { get; init; }

    public IReadOnlyList<StockpileEdit> Edits { get; init; } = [];

    /// <summary>The planned rooms (StockpileRequest.Rooms) the planner raises before the zone edits go on.</summary>
    public IReadOnlyList<PlannedRole> Rooms { get; init; } = [];

    /// <summary>The shelf upgrade: the next shelf for the warehouse. It ranks after every zone edit, so the planner places it only when none stands.</summary>
    public ShelfStep Shelf { get; init; } = new();

    public string Reason { get; init; } = "";
}

/// <summary>
/// The owned zones of one role kind (the role key up to its colon: general, yard, medicine, meals ...; untagged
/// for a zone no planner claimed): how many, their cells and the cells holding things.
/// </summary>
public sealed record StockpileRoleCount
{
    public string Role { get; init; } = "";

    public int Zones { get; init; }

    public int Cells { get; init; }

    public int Used { get; init; }
}

/// <summary>
/// Indexes the cells a new zone may take: observed, walkable, unoccupied, unzoned, unprotected, not taken by
/// another edit this cycle and inside the map.
/// </summary>
internal sealed record StockpileOpen
{
    public required Dictionary<Cell, SiteCell> Cells { get; init; }

    public required HashSet<Cell> Protected { get; init; }

    /// <summary>The ground earlier edits of this pass claimed.</summary>
    public required ReservedGround Taken { get; init; }

    public required Bounds Bounds { get; init; }

    /// <summary>When set, limits the cells to a site's room.</summary>
    public HashSet<Cell>? Only { get; init; }

    public SiteCell CellAt(Cell c) => Cells.TryGetValue(c, out var found) ? found : new SiteCell();

    public bool Ok(Cell p)
    {
        if (!Cells.TryGetValue(p, out var c) || Protected.Contains(p) || Taken.Contains(p) || (Only is not null && !Only.Contains(p))
            || p.X < 0 || p.Z < 0 || p.X >= Bounds.Width || p.Z >= Bounds.Height)
        {
            return false;
        }
        var wk = c.Walkable.TryGetValue(out var walkable);
        var zk = c.Zone.TryGetValue(out var zone);
        return wk && walkable && !c.Occupied() && zk && !zone;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Applies the stockpile zones the departments declare (DeclareStores): a zone is created once at its
    /// store's size, a zone whose role's desired filter or priority changed is patched, a zone whose role's
    /// purpose is gone is deleted, and a store no zone serves is created. Whole-footprint stores grow over
    /// cleared ground and consolidate compatible owned fragments. It acts on the zones the colony created
    /// (store.OwnedZone), role-keyed; a role-less legacy claim stands as created. It is a Standard whose target
    /// is no outstanding work: no zone edit due.
    /// </summary>
    public static readonly ConcernId MaintainStockpiles = new("MaintainStockpiles");

    /// <summary>Ranks MaintainStockpiles with the other upkeep goals.</summary>
    internal const int StockpilePriorityRank = 3;

    /// <summary>The deficit a standing edit ranks with: small, like the tidy's, so it never outranks real shortfalls.</summary>
    internal const double StockpileDeficit = 0.1;

    /// <summary>The used-cell fraction at which a store's standing zones count as full and the department asks layout for further room.</summary>
    public const double StockpileFurtherRoomFill = 0.85;

    /// <summary>
    /// Patches every shelf whose settings differ from its zone's desired ones: its declared store's, else the
    /// zone's own settings. A shelf of a zone that is gone or retiring waits.
    /// </summary>
    private static List<StockpileEdit> StockpileShelfEdits(IReadOnlyList<Store> stores, IReadOnlyList<StockpileZone> zones, IReadOnlyList<StockpileShelf> shelves)
    {
        var byId = new Dictionary<string, StockpileZone>(StringComparer.Ordinal);
        foreach (var z in zones)
        {
            byId[z.Id] = z;
        }
        var sorted = shelves.OrderBy(s => s.Building, StringComparer.Ordinal);
        var output = new List<StockpileEdit>();
        foreach (var s in sorted)
        {
            if (!byId.TryGetValue(s.Zone, out var z) || s.Building.Length == 0)
            {
                continue;
            }
            StockpileFilter? filter = z.Filter;
            var priority = z.Priority;
            if (StoreOfZone(stores, z) is { } store)
            {
                if (store.Retired)
                {
                    continue;
                }
                filter = store.Site.Filter;
                priority = store.Site.Priority;
            }
            if (s.Patched && Equals(s.Filter, filter) && s.Priority == priority)
            {
                continue;
            }
            var hauls = s.Cells * ShelfItemsPerCell;
            output.Add(new StockpileEdit
            {
                Kind = StockpileEditKind.ShelfPatch,
                Zone = s.Building,
                Role = ShelfRole(s.Building),
                Filter = filter,
                Priority = priority,
                Explanation = string.Create(
                    CultureInfo.InvariantCulture,
                    $"shelf {s.Building} in stockpile {z.Id} ({z.Role}): configure like its zone (priority {Wire.Name(priority)}); up to {hauls} stacks may rehome"
                ),
            });
        }
        return output;
    }

    /// <summary>The role kinds of the department-owned food store.</summary>
    private static readonly HashSet<string> FoodStorageRoles = new(StringComparer.Ordinal)
    {
        "meals", "rawfood", "rawmeat", "rawveg", "perishables", "ingredients",
    };

    /// <summary>
    /// "Food storage exists": at least one standing owned zone of a food role kind (a zone the player flagged as
    /// food storage does not count). Unknown until the zone claims are read, never "missing".
    /// </summary>
    public static Fact<bool> FoodStorageStanding(RoundsFacts f)
    {
        if (!f.StockpileZones.TryGetValue(out var counts))
        {
            return Unknown<bool>();
        }
        foreach (var c in counts)
        {
            if (FoodStorageRoles.Contains(c.Role) && c.Zones > 0)
            {
                return Known(true);
            }
        }
        return Known(false);
    }

    /// <summary>Counts the owned zones by role kind, sorted by role.</summary>
    public static List<StockpileRoleCount> StockpileRoleCounts(IReadOnlyList<StockpileZone> zones)
    {
        var byRole = new Dictionary<string, (int Zones, int Cells, int Used)>(StringComparer.Ordinal);
        foreach (var z in zones)
        {
            var kind = StockpileRolePrefix(z.Role);
            if (kind.Length == 0)
            {
                kind = "untagged";
            }
            byRole.TryGetValue(kind, out var count);
            byRole[kind] = (count.Zones + 1, count.Cells + z.Cells.Count, count.Used + z.Used());
        }
        return
        [
            .. byRole
                .Select(kv => new StockpileRoleCount { Role = kv.Key, Zones = kv.Value.Zones, Cells = kv.Value.Cells, Used = kv.Value.Used })
                .OrderBy(c => c.Role, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Proposes this cycle's edits, one per zone, in urgency order (delete, retarget and shelf patch, create;
    /// ties by zone id). Deterministic over its input.
    /// </summary>
    public static StockpileReview PlanStockpileMaintenance(StockpileRequest r)
    {
        var zones = r.Zones.OrderBy(z => z.Id, StringComparer.Ordinal).ToList();
        var open = NewStockpileOpen(r);
        var candidates = new List<StockpileEdit>();
        var touched = new HashSet<string>(StringComparer.Ordinal);

        bool Take(StockpileEdit e, bool ok)
        {
            if (ok && touched.Add(e.Zone))
            {
                candidates.Add(e);
                return true;
            }
            return false;
        }

        var (declared, owned) = DeclaredStoreEdits(zones, r.Stores, open);
        foreach (var e in declared)
        {
            if (e.Kind == StockpileEditKind.Create)
            {
                candidates.Add(e);
            }
            else
            {
                Take(e, true);
            }
        }
        foreach (var id in owned)
        {
            touched.Add(id);
        }
        foreach (var e in StockpileShelfEdits(r.Stores, zones, r.Shelves))
        {
            Take(e, true);
        }
        var rank = new Dictionary<StockpileEditKind, int>
        {
            [StockpileEditKind.Delete] = 0,
            [StockpileEditKind.Retarget] = 1,
            [StockpileEditKind.ShelfPatch] = 1,
            [StockpileEditKind.Grow] = 1,
            [StockpileEditKind.Shrink] = 1,
            [StockpileEditKind.Create] = 2,
        };
        // A moved zone's delete follows its replacement's create.
        int Order(StockpileEdit e) => e.After.Length != 0 ? (rank[StockpileEditKind.Create] * 2) + 1 : rank[e.Kind] * 2;
        var ordered = candidates.OrderBy(Order);
        var created = new HashSet<string>(StringComparer.Ordinal);
        var edits = new List<StockpileEdit>();
        foreach (var e in ordered)
        {
            if (e.After.Length != 0 && !created.Contains(e.After))
            {
                continue;
            }
            edits.Add(e);
            if (e.Kind == StockpileEditKind.Create)
            {
                created.Add(e.Role);
            }
        }
        var shelf = NextWarehouseShelf(r, zones);
        var active = edits.Count > 0 || r.Rooms.Count > 0 || shelf.Kind == ShelfStepKind.Build;
        return new StockpileReview
        {
            Known = true,
            Rooms = r.Rooms,
            Edits = edits,
            Shelf = shelf,
            Active = active,
            Reason = active ? "" : "stockpiles fit their contents",
        };
    }

    /// <summary>
    /// The shelf upgrade: the next shelf for the standing warehouse (the general store) once a Shelf is
    /// buildable. A retired store's zone and a zone whose shelf attempts are spent get none.
    /// </summary>
    private static ShelfStep NextWarehouseShelf(StockpileRequest r, IReadOnlyList<StockpileZone> zones)
    {
        if (!r.ShelfAvailable)
        {
            return new ShelfStep();
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var shelfZones = new List<ShelfZone>();
        foreach (var z in zones)
        {
            if (z.Role != DomainConstants.GeneralRole || (r.ShelfSpent.TryGetValue(z.Id, out var spent) && spent))
            {
                continue;
            }
            if (StoreOfZone(r.Stores, z) is { Retired: true })
            {
                continue;
            }
            ids.Add(z.Id);
            shelfZones.Add(new ShelfZone { Zone = z.Id, Cells = z.Cells, Filter = z.Filter, Priority = z.Priority });
        }
        var request = new ShelfRequest
        {
            Cells = r.Cells,
            Available = true,
            Zones = shelfZones,
            Shelves = [.. r.ShelfRecords.Where(s => ids.Contains(s.Zone))],
        };
        return NextShelfStep(request);
    }

    /// <summary>Deletes a zone whose store retired and retargets one whose store's filter or priority differ from those last applied.</summary>
    private static StockpileEdit? StockpileSettingsEdit(Store s, StockpileZone z)
    {
        if (s.Retired)
        {
            return new StockpileEdit
            {
                Kind = StockpileEditKind.Delete,
                Zone = z.Id,
                Role = z.Role,
                Explanation = string.Create(CultureInfo.InvariantCulture, $"stockpile {z.Id} ({z.Role}): role retired, delete; {z.Used()} used cells rehome"),
            };
        }
        if (Equals(s.Site.Filter, z.Filter) && s.Site.Priority == z.Priority)
        {
            return null;
        }
        return new StockpileEdit
        {
            Kind = StockpileEditKind.Retarget,
            Zone = z.Id,
            Role = z.Role,
            Filter = s.Site.Filter,
            Priority = s.Site.Priority,
            Explanation = string.Create(
                CultureInfo.InvariantCulture,
                $"stockpile {z.Id} ({z.Role}): desired settings changed (priority {Wire.Name(z.Priority)} -> {Wire.Name(s.Site.Priority)}), patch; {z.Used()} used cells may rehome"
            ),
        };
    }

    internal static Cell[] StockpileNeighbours(Cell c) =>
        [new(c.X - 1, c.Z), new(c.X, c.Z - 1), new(c.X, c.Z + 1), new(c.X + 1, c.Z)];

    internal static List<Cell> StockpileSorted(IEnumerable<Cell> cells) => [.. cells.OrderBy(c => c, Comparer<Cell>.Create(CompareCells))];

    private static int CompareCells(Cell a, Cell b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0;

    internal static StockpileOpen NewStockpileOpen(StockpileRequest r)
    {
        var cells = new Dictionary<Cell, SiteCell>(r.Cells.Count);
        foreach (var c in r.Cells)
        {
            cells[c.Cell] = c;
        }
        return new StockpileOpen
        {
            Cells = cells,
            Protected = [.. r.Protected],
            Taken = [],
            Bounds = r.Bounds,
        };
    }
}
