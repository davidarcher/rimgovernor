namespace RimGovernor.Brain.Policy;

// The storage option report: per standing store, what each capacity option costs and what it physically does,
// reported apart. Cost (material, construction work, lead time) and effect (cells, stacks, deterioration avoided)
// are never folded into one value, and an unknown stays unknown with a finding. The report ranks nothing: the
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

    /// <summary>Work ticks (WorkToBuild); unknown when the catalog does not show it.</summary>
    public Fact<double> WorkTicks { get; init; }
}

/// <summary>
/// One planned storage room or yard of the layout plan: its id, role, interior cells, whether it stands in open air
/// (PlannedRoom.Outdoor) and the material the plan still owes for it (PlannedOwedCost; unknown while the plan's
/// owed cost is, empty for a yard, which builds nothing).
/// </summary>
public sealed record PlannedStoreRoom
{
    public int Room { get; init; }

    public PlannedRole Role { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public bool Outdoor { get; init; }

    public Fact<IReadOnlyDictionary<Resource, long>> Material { get; init; }

    /// <summary>The catalog work its planned sites owe (PlannedRoomWork); unknown while the plan or a def's work is.</summary>
    public Fact<double> Work { get; init; }

    /// <summary>A roof is owed: its work has no catalog figure and is not in Work.</summary>
    public bool RoofOwed { get; init; }
}

/// <summary>
/// One capacity option of a store, its cost and effect reported apart. Zone names the zone a placed option stands in,
/// Piece the shelf site, Room the planned room (0 for an unsized room). Material is the build's cost list; WorkTicks
/// its construction work; CellsLost the floor stacks' cells it gives up and CellsTaken the ground it newly takes;
/// Stacks the stacks the store gains; DeteriorationAvoided the HP per day of the store's stranded stacks it would take
/// off their exposed ground. Findings name every unknown.
/// </summary>
public sealed record StorageOption
{
    public StorageOptionKind Kind { get; init; }

    public string Zone { get; init; } = "";

    public InteriorPiece? Piece { get; init; }

    public int Room { get; init; }

    public string Stuff { get; init; } = "";

    public Fact<IReadOnlyDictionary<Resource, long>> Material { get; init; }

    public Fact<double> WorkTicks { get; init; }

    public Fact<int> CellsLost { get; init; }

    public Fact<int> CellsTaken { get; init; }

    public Fact<int> Stacks { get; init; }

    public Fact<double> DeteriorationAvoided { get; init; }

    public EquatableArray<string> Findings { get; init; }

    /// <summary>The option's lead time in days (StorageLeadDays over WorkTicks); unknown while the work is.</summary>
    public Fact<double> LeadDays => StoreRules.StorageLeadDays(WorkTicks);
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
    /// <summary>A shelf build cost the catalog does not give (no Shelf def, or no cost row for its stuff).</summary>
    public const string FindingShelfCostUnknown = "shelf_cost_unknown";

    /// <summary>A build whose WorkToBuild the catalog does not show.</summary>
    public const string FindingWorkUnknown = "work_unknown";

    /// <summary>A planned room's build work is unknown: the plan is unread or a planned site's def shows no WorkToBuild (#3276).</summary>
    public const string FindingRoomWorkUnknown = "room_work_unknown";

    /// <summary>A planned room owes a roof, whose work has no catalog figure: the room's WorkTicks leave it out.</summary>
    public const string FindingRoomRoofWorkUnassessed = "room_roof_work_unassessed";

    /// <summary>A planned room whose owed material is unknown (the plan's owed cost is unread).</summary>
    public const string FindingRoomMaterialUnknown = "room_material_unknown";

    /// <summary>No planned room of the role is waiting to become a store: layout has not sized one.</summary>
    public const string FindingRoomUnsized = "room_unsized";

    /// <summary>The layout plan is unread, so no room option can be read.</summary>
    public const string FindingPlanUnknown = "layout_plan_unknown";

    /// <summary>The stacks gained are unknown (a census or catalog count).</summary>
    public const string FindingStacksUnknown = "stacks_unknown";

    /// <summary>Deterioration avoided is unknown: the loot census, a stranded row's rate or a roof or indoors reading is.</summary>
    public const string FindingDeteriorationUnknown = "deterioration_unknown";

    /// <summary>
    /// The storage option report of every declared, non-retired store a zone serves, in declaration order: its
    /// capacity facts and its options. Zone growth for a whole-footprint store with open footprint ground left; a
    /// shelf per shelf candidate (ShelfCandidates) in its zones; for a warehouse (general) store a successor room per planned
    /// storage room no warehouse zone stands in; for a yard store a further room per such planned yard. With no
    /// such room, one unsized room option, every value unknown. Generation only: nothing is ranked or chosen.
    /// <paramref name="open"/> is the request's open ground; growth is priced over it with no ground taken, as the
    /// ground stood before this pass's edits.
    /// </summary>
    internal static List<StoreOptions> StorageOptions(StockpileRequest r, IReadOnlyList<ShelfCandidate> shelves, StockpileOpen open)
    {
        var output = new List<StoreOptions>();
        var census = open.Cells;
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
                    options.Add(ShelfOption(r, c, stranded, census));
                }
            }
            var prefix = StockpileRules.StockpileRolePrefix(store.Role);
            if (prefix == StockpileRolesConstants.GeneralRole)
            {
                options.AddRange(RoomOptions(r, StorageOptionKind.SuccessorRoom, PlannedRole.Storage, prefix, stranded, census));
            }
            else if (prefix == StockpileRolesConstants.YardRole)
            {
                options.AddRange(RoomOptions(r, StorageOptionKind.FurtherRoom, PlannedRole.Yard, prefix, stranded, census));
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
    private static StorageOption? ZoneGrowthOption(StockpileRequest r, Store store, StockpileZone keep, List<LootItem>? stranded, StockpileOpen untaken)
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
        var avoided = DeteriorationAvoided(r.Items, stranded, Known(add.Count), slots, false, untaken.Cells);
        return new StorageOption
        {
            Kind = StorageOptionKind.ZoneGrowth,
            Zone = keep.Id,
            Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
            WorkTicks = Known(0.0),
            CellsLost = Known(0),
            CellsTaken = Known(add.Count),
            Stacks = Known(add.Count),
            DeteriorationAvoided = avoided,
            Findings = Findings(avoided.IsKnown ? null : FindingDeteriorationUnknown),
        };
    }

    /// <summary>A shelf on a candidate site: the catalog Shelf's cost from its build stuff and its WorkToBuild; its cells' floor stacks lost; the candidate's stacks.</summary>
    private static StorageOption ShelfOption(StockpileRequest r, ShelfCandidate c, List<LootItem>? stranded, Dictionary<Cell, SiteCell> census)
    {
        var cells = PlannedRules.RectCells(c.Piece.Rect).Count;
        var costKnown = r.ShelfCost.TryGetValue(out var cost);
        var material = costKnown ? Known<IReadOnlyDictionary<Resource, long>>(MaterialOf(cost!.Costs)) : Unknown<IReadOnlyDictionary<Resource, long>>();
        var work = costKnown ? cost!.WorkTicks : Unknown<double>();
        var slots = c.Stacks.TryGetValue(out var n) ? Enumerable.Repeat(Known(0.0), Math.Max(0, n)).ToList() : [];
        var avoided = DeteriorationAvoided(r.Items, stranded, c.Stacks, slots, true, census);
        return new StorageOption
        {
            Kind = StorageOptionKind.Shelf,
            Zone = c.Zone,
            Piece = c.Piece,
            Stuff = costKnown ? cost!.Stuff : "",
            Material = material,
            WorkTicks = work,
            CellsLost = Known(cells),
            CellsTaken = Known(0),
            Stacks = c.Stacks,
            DeteriorationAvoided = avoided,
            Findings = Findings(
                costKnown ? null : FindingShelfCostUnknown,
                !costKnown || work.IsKnown ? null : FindingWorkUnknown,
                c.Stacks.IsKnown ? null : FindingStacksUnknown,
                avoided.IsKnown ? null : FindingDeteriorationUnknown),
        };
    }

    /// <summary>
    /// The room options of a store role: one per planned room of the plan role no zone of the store role stands in,
    /// priced from the plan's owed material and work (a yard owes none and its work is FurtherYardWorkTicks; a storage
    /// room's work is its planned sites' catalog work, PlannedStoreRoom.Work, its roof named apart); its interior is the ground taken and the stacks gained, its exposure that of
    /// open air for an outdoor room and none for a roofed one. With none, one unsized option.
    /// </summary>
    private static List<StorageOption> RoomOptions(StockpileRequest r, StorageOptionKind kind, PlannedRole role, string prefix, List<LootItem>? stranded, Dictionary<Cell, SiteCell> census)
    {
        if (!r.StoreRooms.TryGetValue(out var planned))
        {
            return [new StorageOption { Kind = kind, Findings = Findings(FindingPlanUnknown) }];
        }
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
            var work = role == PlannedRole.Yard ? Known(StoreRules.FurtherYardWorkTicks) : room.Work;
            var slots = Enumerable.Repeat(Known(room.Outdoor ? 1.0 : 0.0), room.Cells.Count).ToList();
            var avoided = DeteriorationAvoided(r.Items, stranded, Known(room.Cells.Count), slots, false, census);
            output.Add(new StorageOption
            {
                Kind = kind,
                Room = room.Room,
                Material = room.Material,
                WorkTicks = work,
                CellsLost = Known(0),
                CellsTaken = Known(room.Cells.Count),
                Stacks = Known(room.Cells.Count),
                DeteriorationAvoided = avoided,
                Findings = Findings(
                    room.Material.IsKnown ? null : FindingRoomMaterialUnknown,
                    work.IsKnown ? null : FindingRoomWorkUnknown,
                    role != PlannedRole.Yard && room.RoofOwed ? FindingRoomRoofWorkUnassessed : null,
                    avoided.IsKnown ? null : FindingDeteriorationUnknown),
            });
        }
        if (output.Count == 0)
        {
            output.Add(new StorageOption { Kind = kind, Findings = Findings(FindingRoomUnsized) });
        }
        return output;
    }

    /// <summary>
    /// The HP per day an option saves the store's stranded stacks: the worst rows first (rate times the exposure of
    /// the cell each lies on), up to the stacks gained, each paired with the option's most sheltered slot left and
    /// counted as rate times (exposure now less the slot's). A shelf takes no Chunks, Plants or Buildings row.
    /// Unknown, never zero, when the census, the stacks gained, a row's rate or any exposure is unknown.
    /// </summary>
    internal static Fact<double> DeteriorationAvoided(ItemFacts items, List<LootItem>? stranded, Fact<int> stacks, IReadOnlyList<Fact<double>> slots, bool shelf, IReadOnlyDictionary<Cell, SiteCell> census)
    {
        if (stranded is not { } rows || !stacks.TryGetValue(out var n))
        {
            return Unknown<double>();
        }
        var exposed = new List<(LootItem Row, double Rate, double Exposure)>();
        foreach (var row in rows)
        {
            var def = new Resource(row.Supply.Definition);
            if (shelf && (items.Categories.GetValueOrDefault(def) ?? []).Any(ShelfExcludedCategories.Contains))
            {
                continue;
            }
            if (!items.DeteriorationRate.TryGetValue(def, out var rate) || !CellExposure(census, row.Supply.Cell).TryGetValue(out var exposure))
            {
                return Unknown<double>();
            }
            exposed.Add((row, rate, exposure));
        }
        var taken = exposed
            .OrderByDescending(e => e.Rate * e.Exposure)
            .ThenBy(e => e.Row.Supply.Thing, StringComparer.Ordinal)
            .Take(Math.Max(0, n))
            .ToList();
        if (taken.Count > slots.Count)
        {
            return Unknown<double>();
        }
        var after = new List<double>(slots.Count);
        foreach (var s in slots)
        {
            if (!s.TryGetValue(out var v))
            {
                return Unknown<double>();
            }
            after.Add(v);
        }
        after.Sort();
        var avoided = 0.0;
        for (var i = 0; i < taken.Count; i++)
        {
            avoided += taken[i].Rate * (taken[i].Exposure - after[i]);
        }
        return Known(avoided);
    }

    /// <summary>
    /// A cell's deterioration exposure, the factor the game multiplies an item's DeteriorationRate by
    /// (SteadyEnvironmentEffects): 0.5 unroofed plus 0.5 not indoors. Unknown when the census does not hold the cell.
    /// </summary>
    internal static Fact<double> CellExposure(IReadOnlyDictionary<Cell, SiteCell> census, Cell cell)
    {
        if (!census.TryGetValue(cell, out var c))
        {
            return Unknown<double>();
        }
        return Known((c.Roofed ? 0.0 : 0.5) + (c.Indoors ? 0.0 : 0.5));
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
