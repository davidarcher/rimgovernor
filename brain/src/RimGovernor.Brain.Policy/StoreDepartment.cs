namespace RimGovernor.Brain.Policy;

// A department that owns stockpiles is an entity: it declares its Stores and publishes its RoomDemand, and
// The admin/stockpiles Need applies every declaration in one pass. A department that owns no store stays a grouping
// tag. A store is declared here once, and the one definition serves create, retarget, geometry maintenance and
// retirement.

/// <summary>
/// One declared stockpile: its site (role, planned room or clean rectangle inside it, filter, priority) and what
/// the department does when it is gone. Whole-footprint stores fill cleared site ground; fixed-size
/// stores keep their requested rectangle.
/// </summary>
public sealed record Store
{
    public StoreSite Site { get; init; } = new();

    /// <summary>
    /// States the store's purpose is gone (a bench demolished): the zones of its role are deleted. Absence is
    /// never retirement, so a department that cannot read its room yet simply declares nothing.
    /// </summary>
    public bool Retired { get; init; }

    public string Role => Site.Role;

    internal bool Serves(StockpileZone z) => Site.Serves(z);

    /// <summary>Reports a zone carrying the store's role (the prefix, or the whole key for an exact site), wherever it stands.</summary>
    internal bool RoleOf(StockpileZone z)
    {
        if (z.Role.Length == 0)
        {
            return false;
        }
        if (Site.Exact)
        {
            return z.Role == Site.Role;
        }
        return StockpileRules.StockpileRolePrefix(z.Role) == StockpileRules.StockpileRolePrefix(Site.Role);
    }
}

/// <summary>
/// A department that declares stockpiles. The view is the storage planner's request: layout plan, room census,
/// open ground and standing zones.
/// </summary>
public interface IStoreOwner
{
    /// <summary>The department's declared stores, most important first.</summary>
    IReadOnlyList<Store> Stores(StoreView view);

    /// <summary>
    /// The department's ask of layout from the view (the training range and ring). The yard's further-room ask
    /// reads capacity facts the view lacks, so it is YardDemand over the maintenance request.
    /// </summary>
    RoomDemand RoomDemand(StoreView view);
}

/// <summary>Every owner's stores and room demand, merged.</summary>
public sealed record StoreDeclaration
{
    public IReadOnlyList<Store> Stores { get; init; } = [];

    public RoomDemand Demand { get; init; } = new();
}

public static partial class StoreRules
{
    /// <summary>The one registry of departments that declare stores.</summary>
    private static readonly IReadOnlyList<IStoreOwner> StoreOwners =
    [
        new StorageOwner(),
        new MilitaryOwner(),
        new IndustryOwner(),
        new BurialOwner(),
        new FoodOwner(),
        new MedicalOwner(),
    ];

    /// <summary>
    /// The store's stranded stacks: unforbidden, safe loot rows with zero storage headroom whose accepting storage
    /// groups include a zone serving the store or a shelf in one. Such a stack can be hauled nowhere now, and this
    /// store would take it. Headroom is read as the haul backlog reads it (WorkAssignment). Unknown, never zero, when
    /// the census is unread, when a row's headroom is unknown (it could belong to any store) or when a zero-headroom
    /// row of unknown safety is accepted by this store.
    /// </summary>
    public static Fact<long> StrandedStacks(StockpileRequest request, Store store) =>
        StrandedRows(request, store) is { } rows ? Known((long)rows.Count) : Unknown<long>();

    /// <summary>The store's stranded loot rows (<see cref="StrandedStacks"/>), one stack each; null (unread) on the same terms.</summary>
    internal static List<LootItem>? StrandedRows(StockpileRequest request, Store store) => StrandedRows(request, StoreGroups(request, store));

    /// <summary>
    /// The stranded loot rows one of whose accepting storage groups is in <paramref name="groups"/>; null (unread) on the terms of
    /// <see cref="StrandedStacks"/>. With <paramref name="reachedOnly"/> (a planned store's stock, #3835) a row of unknown safety,
    /// which no colonist reaches now (HaulingSafety.Safe), is left out: no hauler can carry it to the store.
    /// </summary>
    private static List<LootItem>? StrandedRows(StockpileRequest request, HashSet<string> groups, bool reachedOnly = false)
    {
        var stranded = new List<LootItem>();
        foreach (var row in request.Loot)
        {
            if (row.Forbidden)
            {
                continue;
            }
            if (row.StorageHeadroom > 0 || row.IsExposed || !row.AcceptedBy.Any(groups.Contains))
            {
                continue;
            }
            if (row.IsUnreached)
            {
                if (reachedOnly)
                {
                    continue;
                }
                return null;
            }
            stranded.Add(row);
        }
        return stranded;
    }

    /// <summary>
    /// The stock each planned storage room, yard and meal closet would keep (#3798), by room id: its interior's cells (one
    /// stack each) and the stranded rows whose accepting storage groups include a zone of the role of a store sited on its
    /// region (or a shelf in one), each row once. Zones of one role carry one filter, so a room whose own zone is not yet
    /// laid is read through its role's standing zones, as StorageOptions prices a successor room. A room no store is
    /// sited on keeps nothing, and a stack no colonist reaches is no room's; a room whose rows are unread (the loot census or a
    /// row's headroom) has no entry.
    /// </summary>
    public static IReadOnlyDictionary<int, PlannedStock> PlannedStoreStock(StockpileRequest request, IReadOnlyList<PlannedRoom> rooms)
    {
        var output = new Dictionary<int, PlannedStock>();
        foreach (var room in rooms)
        {
            if (room.Role is not (PlannedRole.Storage or PlannedRole.Yard or PlannedRole.MealCloset))
            {
                continue;
            }
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var store in request.Stores.Where(s => !s.Retired && s.Site.Region == room.Region))
            {
                groups.UnionWith(request.Zones.Where(store.RoleOf).Select(z => z.Id));
            }
            foreach (var s in request.Shelves)
            {
                if (groups.Contains(s.Zone))
                {
                    groups.Add(s.Building);
                }
            }
            if (StrandedRows(request, groups, reachedOnly: true) is { } rows)
            {
                output[room.Id] = new PlannedStock(room.Region.Area(), EquatableArray.From(rows));
            }
        }
        return output;
    }

    /// <summary>The store's storage groups (native load ids): the owned zones serving it and the built shelves in them.</summary>
    internal static HashSet<string> StoreGroups(StockpileRequest request, Store store)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var z in request.Zones)
        {
            if (store.Serves(z))
            {
                groups.Add(z.Id);
            }
        }
        foreach (var s in request.Shelves)
        {
            if (groups.Contains(s.Zone))
            {
                groups.Add(s.Building);
            }
        }
        return groups;
    }

    /// <summary>Collects the registered departments' declarations.</summary>
    public static StoreDeclaration DeclareStores(StoreView view) => DeclareStores(StoreOwners, view);

    internal static StoreDeclaration DeclareStores(IReadOnlyList<IStoreOwner> owners, StoreView view)
    {
        var stores = new List<Store>();
        var demand = new RoomDemand();
        foreach (var o in owners)
        {
            stores.AddRange(o.Stores(view));
            var got = o.RoomDemand(view);
            demand = demand with
            {
                Yard = Math.Max(demand.Yard, got.Yard),
                Ranges = Math.Max(demand.Ranges, got.Ranges),
                Rings = Math.Max(demand.Rings, got.Rings),
            };
        }
        return new StoreDeclaration { Stores = stores, Demand = demand };
    }

    /// <summary>
    /// The edits of the declared stores, one set of rules: a zone outside every live site of its role is
    /// deleted once the replacement stands (create before delete), a zone of a retired role is deleted, one
    /// whose filter or priority differs is retargeted, and a store no zone serves gets its zone created.
    /// Whole-footprint stores expand and consolidate as their ground clears. Zones handled here are returned in
    /// touched.
    /// </summary>
    internal static (List<StockpileEdit> Edits, HashSet<string> Touched) DeclaredStoreEdits(
        IReadOnlyList<StockpileZone> zones,
        IReadOnlyList<Store> stores,
        StockpileOpen open
    )
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var edits = new List<StockpileEdit>();
        var live = stores.Where(s => !s.Retired).Select(s => s.Site).ToList();
        foreach (var e in StockpileRules.StoreSiteMoves(zones, live))
        {
            touched.Add(e.Zone);
            edits.Add(e);
        }
        foreach (var z in zones)
        {
            foreach (var s in stores)
            {
                if (!s.RoleOf(z) || touched.Contains(z.Id))
                {
                    continue;
                }
                if (s.Retired || s.Serves(z))
                {
                    touched.Add(z.Id);
                    if (StockpileRules.StockpileSettingsEdit(s, z) is { } e)
                    {
                        edits.Add(e);
                    }
                    break;
                }
            }
        }
        var shrinks = StockpileRules.StoreShrinkEdits(zones, live, open, edits);
        edits.AddRange(shrinks);
        foreach (var e in shrinks)
        {
            touched.Add(e.Zone);
        }
        edits.AddRange(StockpileRules.StoreGeometryEdits(zones, live, open, edits.ToList()));
        edits.AddRange(StockpileRules.StoreSiteEdits(zones, live, open));
        return (edits, touched);
    }

    /// <summary>The declared store a zone belongs to: the one serving it, else the first of its role.</summary>
    internal static Store? StoreOfZone(IReadOnlyList<Store> stores, StockpileZone z)
    {
        Store? first = null;
        foreach (var s in stores)
        {
            if (!s.RoleOf(z))
            {
                continue;
            }
            if (s.Serves(z))
            {
                return s;
            }
            first ??= s;
        }
        return first;
    }
}
