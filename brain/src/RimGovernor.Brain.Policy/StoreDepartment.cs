namespace RimGovernor.Brain.Policy;

// A department that owns stockpiles is an entity: it declares its Stores and publishes its RoomDemand, and
// MaintainStockpiles applies every declaration in one pass. A department that owns no store stays a grouping
// tag. A store is declared here once, and the one definition serves create, retarget, geometry maintenance and
// retirement.

/// <summary>
/// One declared stockpile: its site (role, planned room or clean rectangle inside it, filter, priority) and what
/// the department does when it is full or gone. Whole-footprint stores fill cleared site ground; fixed-size
/// stores keep their requested rectangle. (Go embeds <c>StoreSite</c>; here it is the <see cref="Site"/> member.)
/// </summary>
public sealed record Store
{
    public StoreSite Site { get; init; } = new();

    /// <summary>
    /// The planned room the department asks layout for when the store's standing zones are at
    /// StockpileFurtherRoomFill (PlannedStorage, PlannedArmory or PlannedWardrobe); null asks for none.
    /// </summary>
    public PlannedRole? Further { get; init; }

    /// <summary>
    /// States the store's purpose is gone (a bench demolished): the zones of its role are deleted. Absence is
    /// never retirement, so a department that cannot read its room yet simply declares nothing.
    /// </summary>
    public bool Retired { get; init; }

    public string Role => Site.Role;

    internal bool Serves(StockpileZone z) => Site.Serves(z);

    /// <summary>Reads the store against the standing zones.</summary>
    public StoreReading Reading(IReadOnlyList<StockpileZone> zones)
    {
        var standing = false;
        var full = true;
        foreach (var z in zones)
        {
            if (Serves(z))
            {
                standing = true;
                full = full && z.Fill() >= PolicyFunctions.StockpileFurtherRoomFill;
            }
        }
        return new StoreReading(standing, full && standing);
    }

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
        return PolicyFunctions.StockpileRolePrefix(z.Role) == PolicyFunctions.StockpileRolePrefix(Site.Role);
    }
}

/// <summary>
/// A department that declares stockpiles. The view is the storage planner's request: layout plan, room census,
/// open ground and standing zones.
/// </summary>
public interface IStoreOwner
{
    Department Department { get; }

    /// <summary>The department's declared stores, most important first.</summary>
    IReadOnlyList<Store> Stores(StoreView view);

    /// <summary>The department's ask of layout, from capacity: usually DeclaredDemand over its own stores.</summary>
    RoomDemand RoomDemand(StoreView view);
}

/// <summary>An owner that asks layout for rooms even before any store stands (Go's optional <c>RoomsAsked</c> method).</summary>
public interface IRoomsAsker
{
    IReadOnlyList<PlannedRole> RoomsAsked();
}

/// <summary>An owner that can report a store it could not site (Go's optional <c>StoreErr</c> method).</summary>
public interface IStoreErrorSource
{
    Exception? StoreErr(StoreView view);
}

/// <summary>Every owner's stores and room demand, merged.</summary>
public sealed record StoreDeclaration
{
    public IReadOnlyList<Store> Stores { get; init; } = [];

    public RoomDemand Demand { get; init; } = new();

    /// <summary>Joins what an owner could not site (ErrArmoryNearPrison).</summary>
    public Exception? Err { get; init; }

    /// <summary>The room roles whose demand a declared store owns.</summary>
    internal HashSet<PlannedRole> Covers { get; init; } = [];

    /// <summary>
    /// The layout demand once the declared stores answer for their rooms: a covered room takes the declared
    /// reading, the rest keeps old.
    /// </summary>
    public RoomDemand Apply(RoomDemand old)
    {
        var next = old;
        if (Covers.Contains(PlannedRole.Storage))
        {
            next = next with { Storage = Demand.Storage, StorageIdle = Demand.StorageIdle };
        }
        if (Covers.Contains(PlannedRole.Yard))
        {
            next = next with { Yard = Demand.Yard };
        }
        if (Covers.Contains(PlannedRole.Armory))
        {
            next = next with { Armory = Demand.Armory };
        }
        if (Covers.Contains(PlannedRole.Wardrobe))
        {
            next = next with { Wardrobe = Demand.Wardrobe };
        }
        return next with
        {
            Graveyards = Demand.Graveyards,
            Ranges = Demand.Ranges,
            Rings = Demand.Rings,
            Known = old.Known || Demand.Known,
        };
    }
}

/// <summary>A store's capacity now: Standing when a zone serves it, Full when every serving zone is at StockpileFurtherRoomFill. A sized-once store has no room to grow onto, so full is the whole reading.</summary>
public readonly record struct StoreReading(bool Standing, bool Full);

public static partial class PolicyFunctions
{
    /// <summary>The one registry of departments that declare stores.</summary>
    private static readonly IReadOnlyList<IStoreOwner> StoreOwners =
    [
        new StorageOwner(),
        new ShelterOwner(),
        new MilitaryOwner(),
        new IndustryOwner(),
        new IncinerationOwner(),
        new BurialOwner(),
        new AnimalOwner(),
        new FoodOwner(),
        new MedicalOwner(),
    ];

    /// <summary>Collects the registered departments' declarations.</summary>
    public static StoreDeclaration DeclareStores(StoreView view) => DeclareStores(StoreOwners, view);

    internal static StoreDeclaration DeclareStores(IReadOnlyList<IStoreOwner> owners, StoreView view)
    {
        var covers = new HashSet<PlannedRole>();
        var stores = new List<Store>();
        var demand = new RoomDemand();
        var errors = new List<Exception>();
        foreach (var o in owners)
        {
            var owned = o.Stores(view);
            foreach (var s in owned)
            {
                if (s.Further is { } further && !s.Retired)
                {
                    covers.Add(further);
                }
            }
            stores.AddRange(owned);
            if (o is IRoomsAsker asker)
            {
                foreach (var role in asker.RoomsAsked())
                {
                    covers.Add(role);
                }
            }
            if (o is IStoreErrorSource source && source.StoreErr(view) is { } err)
            {
                errors.Add(err);
            }
            var got = o.RoomDemand(view);
            demand = demand with
            {
                Armory = demand.Armory || got.Armory,
                Wardrobe = demand.Wardrobe || got.Wardrobe,
                Storage = Math.Max(demand.Storage, got.Storage),
                Yard = Math.Max(demand.Yard, got.Yard),
                StorageIdle = demand.StorageIdle || got.StorageIdle,
                Graveyards = Math.Max(demand.Graveyards, got.Graveyards),
                Ranges = Math.Max(demand.Ranges, got.Ranges),
                Rings = Math.Max(demand.Rings, got.Rings),
                Known = demand.Known || got.Known,
            };
        }
        Exception? joined = errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException(errors),
        };
        return new StoreDeclaration { Stores = stores, Demand = demand, Err = joined, Covers = covers };
    }

    /// <summary>
    /// The capacity-based room demand of stores: an armory or wardrobe store at capacity asks for its room
    /// (Known: a gear store reads the gear census, a storage or yard store does not); storage and yard stores
    /// ask for one more room than the plan holds once every one stands and is full. A store whose room does not
    /// stand yet is a wait, never a demand or an idle reading.
    /// </summary>
    public static RoomDemand DeclaredDemand(StoreView view, IReadOnlyList<Store> stores)
    {
        var demand = new RoomDemand();
        var storage = 0;
        var full = 0;
        var idle = false;
        var pending = false;
        var yards = 0;
        var fullYards = 0;
        var waiting = false;
        foreach (var s in stores)
        {
            if (s.Retired || s.Further is null)
            {
                continue;
            }
            var reading = s.Reading(view.Zones);
            switch (s.Further)
            {
                case PlannedRole.Storage:
                    storage++;
                    if (!reading.Standing)
                    {
                        pending = true;
                    }
                    else if (reading.Full)
                    {
                        full++;
                    }
                    else
                    {
                        idle = true;
                    }
                    break;
                case PlannedRole.Yard:
                    yards++;
                    if (reading.Full)
                    {
                        fullYards++;
                    }
                    waiting = waiting || !reading.Standing;
                    break;
                case PlannedRole.Armory:
                    demand = demand with { Known = true, Armory = demand.Armory || reading.Full };
                    break;
                case PlannedRole.Wardrobe:
                    demand = demand with { Known = true, Wardrobe = demand.Wardrobe || reading.Full };
                    break;
            }
        }
        demand = demand with { StorageIdle = idle };
        if (storage > 0 && !pending && !idle && full == storage)
        {
            demand = demand with { Storage = view.StorageRoomChains() + 1 };
        }
        if (yards > 0 && !waiting && fullYards == yards)
        {
            demand = demand with { Yard = view.Layout!.YardRooms().Count + 1 };
        }
        return demand;
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
        foreach (var e in StoreSiteMoves(zones, live))
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
                    if (StockpileSettingsEdit(s, z) is { } e)
                    {
                        edits.Add(e);
                    }
                    break;
                }
            }
        }
        var shrinks = StoreShrinkEdits(zones, live, open, edits);
        edits.AddRange(shrinks);
        foreach (var e in shrinks)
        {
            touched.Add(e.Zone);
        }
        edits.AddRange(StoreGeometryEdits(zones, live, open, edits.ToList()));
        edits.AddRange(StoreSiteEdits(zones, live, open));
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
