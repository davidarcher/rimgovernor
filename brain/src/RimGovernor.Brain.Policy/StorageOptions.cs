namespace RimGovernor.Brain.Policy;

// The storage option report: per standing store, what each capacity option costs and what it physically does,
// reported apart. Cost (material, construction work, lead time) and effect (cells, stacks, deterioration avoided)
// are never folded into one value, and missing required observations fail the review. The report ranks nothing: the
// placed options (zone growth, shelf) feed the storage portfolio (#3239); the room options reach layout through
// the yard's runway and stranded signal and the station plan's successor rooms.

public enum StorageOptionKind
{
    /// <summary>More zone cells: the open, reachable footprint ground a whole-footprint store can still take.</summary>
    ZoneGrowth,

    /// <summary>One shelf on a ShelfCandidates site inside the store's zone.</summary>
    Shelf,

    /// <summary>A further storage room for a warehouse (general) store: a planned storage room not yet a store.</summary>
    SuccessorRoom,

    /// <summary>A further materials yard for a yard store: a planned yard not yet a store.</summary>
    FurtherRoom,
}

/// <summary>A def's catalog build cost: the stuff it is built from (empty for a def not made from stuff), its cost list made from it and its WorkToBuild.</summary>
public sealed record StorageBuildCost
{
    public string Stuff { get; init; } = "";

    public IReadOnlyList<Amount> Costs { get; init; } = [];

    /// <summary>Work ticks (WorkToBuild); required from the catalog.</summary>
    public double WorkTicks { get; init; }
}

/// <summary>
/// One planned storage room or yard of the layout plan: its id, role, interior cells, whether it stands in open air
/// (PlannedRoom.Outdoor) and the material the plan still owes for it (PlannedOwedCost; empty for a yard, which builds nothing).
/// </summary>
public sealed record PlannedStoreRoom
{
    public int Room { get; init; }

    public PlannedRole Role { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public bool Outdoor { get; init; }

    public IReadOnlyDictionary<Resource, long> Material { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The catalog work its planned sites owe (PlannedRoomWork); required from the plan and catalog.</summary>
    public double Work { get; init; }

    /// <summary>A roof is owed: its work has no catalog figure and is not in Work.</summary>
    public bool RoofOwed { get; init; }
}

/// <summary>
/// One capacity option of a store, its cost and effect reported apart. Zone names the zone a placed option stands in,
/// Piece the shelf site, Room the planned room. Material is the build's cost list; WorkTicks
/// its construction work; CellsLost the floor stacks' cells it gives up and CellsTaken the ground it newly takes;
/// Stacks the stacks the store gains; DeteriorationAvoided the HP per day of the store's stranded stacks it would take
/// off their exposed ground. Findings name work excluded from the catalog cost.
/// </summary>
public sealed record StorageOption
{
    public StorageOptionKind Kind { get; init; }

    public string Zone { get; init; } = "";

    public InteriorPiece? Piece { get; init; }

    public int Room { get; init; }

    public string Stuff { get; init; } = "";

    public IReadOnlyDictionary<Resource, long> Material { get; init; } = new Dictionary<Resource, long>();

    public double WorkTicks { get; init; }

    public int CellsLost { get; init; }

    public int CellsTaken { get; init; }

    public int Stacks { get; init; }

    public double DeteriorationAvoided { get; init; }

    public EquatableArray<string> Findings { get; init; }

    /// <summary>The option's lead time in days (StorageLeadDays over WorkTicks); computed from required work.</summary>
    public double LeadDays => StoreRules.StorageLeadDays(WorkTicks);
}

/// <summary>One standing store's capacity facts and its options: the store's role, the zones serving it (by id), its StoreCapacity and every option.</summary>
public sealed record StoreOptions
{
    public string Role { get; init; } = "";

    public EquatableArray<string> Zones { get; init; }

    public required StoreCapacity Capacity { get; init; }

    public IReadOnlyList<StorageOption> Options { get; init; } = [];
}

public static partial class StorageRules
{

    /// <summary>A planned room owes a roof, whose work has no catalog figure: the room's WorkTicks leave it out.</summary>
    public const string FindingRoomRoofWorkUnassessed = "room_roof_work_unassessed";

    /// <summary>
    /// The storage option report of every declared, non-retired store a zone serves, in declaration order: its
    /// capacity facts and its options. Zone growth for a whole-footprint store with open footprint ground left; a
    /// shelf per shelf candidate (ShelfCandidates) in its zones; for a warehouse (general) store a successor room per planned
    /// storage room no warehouse zone stands in; for a yard store a further room per such planned yard. With no such planned room, there is no room candidate. Generation only: nothing is ranked or chosen.
    /// <paramref name="open"/> is the request's open ground; growth is priced over it with no ground taken, as the
    /// ground stood before this pass's edits.
    /// </summary>
    internal static List<StoreOptions> StorageOptions(StockpileRequest r, IReadOnlyList<ShelfCandidate> shelves, StockpileOpen open)
    {
        var output = new List<StoreOptions>();
        var untaken = open with { Taken = [] };
        foreach (var store in r.Stores)
        {
            if (store.Retired)
            {
                continue;
            }
            var zones = r.Zones.Where(store.Serves).OrderBy(z => z.Id, StringComparer.Ordinal).ToList();
            if (zones.Count == 0)
            {
                continue;
            }
            var stranded = StoreRules.StrandedRows(r, store);
            var options = new List<StorageOption>();
            if (ZoneGrowthOption(r, store, zones[0], stranded, untaken) is { } growth)
            {
                options.Add(growth);
            }
            var ids = new HashSet<string>(zones.Select(z => z.Id), StringComparer.Ordinal);
            foreach (var c in shelves)
            {
                if (ids.Contains(c.Zone))
                {
                    options.Add(ShelfOption(r, c, stranded));
                }
            }
            var prefix = StockpileRules.StockpileRolePrefix(store.Role);
            if (prefix == StockpileRolesConstants.GeneralRole)
            {
                options.AddRange(RoomOptions(r, StorageOptionKind.SuccessorRoom, PlannedRole.Storage, prefix, stranded));
            }
            else if (prefix == StockpileRolesConstants.YardRole)
            {
                options.AddRange(RoomOptions(r, StorageOptionKind.FurtherRoom, PlannedRole.Yard, prefix, stranded));
            }
            output.Add(new StoreOptions
            {
                Role = store.Role,
                Zones = EquatableArray.From(zones.Select(z => z.Id).ToList()),
                Capacity = StoreRules.StoreCapacity(r, store),
                Options = options,
            });
        }
        return output;
    }

    /// <summary>Zone growth: the store's open, reachable footprint cells (StoreGrowth), no material and no work, one stack per new cell; null when none is left or the store is a fixed-size patch.</summary>
    private static StorageOption? ZoneGrowthOption(StockpileRequest r, Store store, StockpileZone keep, List<LootItem> stranded, StockpileOpen untaken)
    {
        if (!store.Site.WholeFootprint())
        {
            return null;
        }
        var (add, _, _) = StockpileRules.StoreGrowth(store.Site, keep, untaken);
        if (add.Count == 0)
        {
            return null;
        }
        var slots = add.Select(c => CellExposure(untaken.Cells, c)).ToList();
        var avoided = DeteriorationAvoided(r.Items, stranded, add.Count, slots, false);
        return new StorageOption
        {
            Kind = StorageOptionKind.ZoneGrowth,
            Zone = keep.Id,
            Material = new Dictionary<Resource, long>(),
            WorkTicks = 0.0,
            CellsLost = 0,
            CellsTaken = add.Count,
            Stacks = add.Count,
            DeteriorationAvoided = avoided,
            Findings = [],
        };
    }

    /// <summary>A shelf on a candidate site: the catalog Shelf's cost from its build stuff and its WorkToBuild; its cells' floor stacks lost; the candidate's stacks.</summary>
    private static StorageOption ShelfOption(StockpileRequest r, ShelfCandidate c, List<LootItem> stranded)
    {
        var cells = PlannedRules.RectCells(c.Piece.Rect).Count;
        var cost = r.ShelfCost ?? throw new RequiredFactException("shelf build cost");
        var material = MaterialOf(cost.Costs);
        var work = cost.WorkTicks;
        var slots = Enumerable.Repeat(0.0, Math.Max(0, c.Stacks)).ToList();
        var avoided = DeteriorationAvoided(r.Items, stranded, c.Stacks, slots, true);
        return new StorageOption
        {
            Kind = StorageOptionKind.Shelf,
            Zone = c.Zone,
            Piece = c.Piece,
            Stuff = cost.Stuff,
            Material = material,
            WorkTicks = work,
            CellsLost = cells,
            CellsTaken = 0,
            Stacks = c.Stacks,
            DeteriorationAvoided = avoided,
            Findings = [],
        };
    }

    /// <summary>
    /// The room options of a store role: one per planned room of the plan role no zone of the store role stands in,
    /// priced from the plan's owed material and work (a yard owes none and its work is FurtherYardWorkTicks; a storage
    /// room's work is its planned sites' catalog work, PlannedStoreRoom.Work, its roof named apart); its interior is the ground taken and the stacks gained, its exposure that of
    /// open air for an outdoor room and none for a roofed one. With none, no room candidate.
    /// </summary>
    private static List<StorageOption> RoomOptions(StockpileRequest r, StorageOptionKind kind, PlannedRole role, string prefix, List<LootItem> stranded)
    {
        if (r.Layout is not StorageLayout.Planned(_, var planned)) return [];
        var zoned = new HashSet<Cell>();
        foreach (var z in r.Zones)
        {
            if (StockpileRules.StockpileRolePrefix(z.Role) == prefix)
            {
                zoned.UnionWith(z.Cells);
            }
        }
        var output = new List<StorageOption>();
        foreach (var room in planned)
        {
            if (room.Role != role || room.Cells.Any(zoned.Contains))
            {
                continue;
            }
            var work = role == PlannedRole.Yard ? StoreRules.FurtherYardWorkTicks : room.Work;
            var slots = Enumerable.Repeat(room.Outdoor ? 1.0 : 0.0, room.Cells.Count).ToList();
            var avoided = DeteriorationAvoided(r.Items, stranded, room.Cells.Count, slots, false);
            output.Add(new StorageOption
            {
                Kind = kind,
                Room = room.Room,
                Material = room.Material,
                WorkTicks = work,
                CellsLost = 0,
                CellsTaken = room.Cells.Count,
                Stacks = room.Cells.Count,
                DeteriorationAvoided = avoided,
                Findings = Findings(

                    role != PlannedRole.Yard && room.RoofOwed ? FindingRoomRoofWorkUnassessed : null),
            });
        }

        return output;
    }

    /// <summary>
    /// The HP per day an option saves the store's stranded stacks: the worst rows first (rate times the exposure of
    /// the cell each lies on), up to the stacks gained, each paired with the option's most sheltered slot left and
    /// counted as rate times (exposure now less the slot's). A shelf takes no Chunks, Plants or Buildings row.
    /// Required counts and exposure cells fail the review when absent; native reads each stack's actual-stuff rate.
    /// </summary>
    internal static double DeteriorationAvoided(ItemFacts items, List<LootItem> stranded, int stacks, IReadOnlyList<double> slots, bool shelf)
    {
        var rows = stranded;
        var n = stacks;
        var exposed = new List<(LootItem Row, double Rate, double Exposure)>();
        foreach (var row in rows)
        {
            var def = new Resource(row.Supply.Definition);
            if (shelf && (items.Categories.GetValueOrDefault(def) ?? []).Any(ShelfExcludedCategories.Contains))
            {
                continue;
            }
            // Loot spans the whole map, including cells outside the planning census. Native already read this stack's exposure.
            var rate = row.Deterioration?.BaseRatePerDay ?? 0;
            var exposure = row.Deterioration is { } d ? (d.Roofed ? 0.0 : 0.5) + (d.Outdoors ? 0.5 : 0.0) : 0;
            exposed.Add((row, rate, exposure));
        }
        var taken = exposed
            .OrderByDescending(e => e.Rate * e.Exposure)
            .ThenBy(e => e.Row.Supply.Thing, StringComparer.Ordinal)
            .Take(Math.Max(0, n))
            .ToList();
        if (taken.Count > slots.Count)
        {
            throw new RequiredFactException("storage exposure slots");
        }
        var after = new List<double>(slots.Count);
        after.AddRange(slots);
        after.Sort();
        var avoided = 0.0;
        for (var i = 0; i < taken.Count; i++)
        {
            avoided += taken[i].Rate * (taken[i].Exposure - after[i]);
        }
        return avoided;
    }

    /// <summary>
    /// A cell's deterioration exposure, the factor the game multiplies an item's DeteriorationRate by
    /// (SteadyEnvironmentEffects): 0.5 unroofed plus 0.5 not indoors. A missing census cell raises RequiredFactException.
    /// </summary>
    internal static double CellExposure(IReadOnlyDictionary<Cell, SiteCell> census, Cell cell)
    {
        if (!census.TryGetValue(cell, out var c))
        {
            throw new RequiredFactException("storage exposure slots");
        }
        return (c.Roofed ? 0.0 : 0.5) + (c.Indoors ? 0.0 : 0.5);
    }

    private static Dictionary<Resource, long> MaterialOf(IReadOnlyList<Amount> costs)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var a in costs)
        {
            if (a.Count > 0)
            {
                output[a.Resource] = output.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        return output;
    }

    private static EquatableArray<string> Findings(params string?[] findings) => EquatableArray.From(findings.OfType<string>().ToList());
}

/// <summary>First-plan derivation runs after the initial review, so its absence is a lifecycle state rather than missing native geometry.</summary>
public abstract record StorageLayout
{
    private StorageLayout() { }
    public sealed record AwaitingFirstPlan : StorageLayout;
    public sealed record Planned(EquatableArray<Cell> Walkways, IReadOnlyList<PlannedStoreRoom> Rooms) : StorageLayout;
}
