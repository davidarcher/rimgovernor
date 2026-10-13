namespace RimGovernor.Brain.Policy;

// Beauty levers: an owned bedroom below its target whose weakest stat is beauty gets, one change at a time,
//   - a plant pot on free floor (native growers sow its default plant);
//   - a prettier floor: every cell whose terrain is less beautiful than the most beautiful affordable floor
//     the colony can lay, up to one plan's worth of cells.
// The template furniture (NextRoomUpgrade) and bed (NextBedReplacement) levers go first; these only add beauty.

/// <summary>Installs Packed (a packed item's id) at Anchor, Rot.</summary>
public sealed record SculptureStep
{
    public string Room { get; init; } = "";

    public string Packed { get; init; } = "";

    public Cell Anchor { get; init; }

    public Rotation Rot { get; init; }
}

/// <summary>A packed sculpture in stock: its id, the sculpture definition inside, its quality (QualityCategory ordinal, -1 unknown) and market value.</summary>
public sealed record PackedSculpture
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public int Quality { get; init; }

    public double MarketValue { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The plant pot the beauty lever places.</summary>
    public const string PlantPotDefinition = "PlantPot";

    /// <summary>Bounds one floor upgrade plan, as MaintainFlooring's MaxCellsPerPlan does.</summary>
    internal const int MaxBeautyFloorCells = 24;

    /// <summary>
    /// The sculpture lever, after pots and floors: a finished packed small sculpture installed on free floor
    /// in the room. The sculpture itself is MaintainArt's pinned bill. Sculptures pack as the generic
    /// MinifiedThing (Buildings_Art.xml), so the packed read is filtered by inner definition.
    /// </summary>
    public const string PackedSculptureDefinition = "MinifiedThing";

    /// <summary>
    /// The first (by room id) beauty upgrade due, false when none. A floor upgrade carries its cells in Cells
    /// (Def placed at each, North); a plant pot is one piece at Anchor.
    /// </summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextBeautyUpgrade(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        Func<string, bool> available,
        Fact<FlooringObservation> flooring,
        FlooringFacts floors,
        RoomGate gate)
    {
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        var floorRooms = new Dictionary<string, FloorRoom>(StringComparer.Ordinal);
        var fk = flooring.TryGetValue(out var fo);
        if (fk)
        {
            foreach (var r in fo!.Rooms)
            {
                floorRooms[r.Id] = r;
            }
        }
        foreach (var id in BeautyRooms(obs, targets))
        {
            if (!furniture.TryGetValue(id, out var room))
            {
                continue;
            }
            var pot = false;
            foreach (var p in room.Pieces)
            {
                pot = pot || p.Def == PlantPotDefinition;
            }
            if (!pot && available(PlantPotDefinition) && gate.PieceAllowed(targets[id].Owners, PlantPotDefinition))
            {
                var (cell, _, ok) = FreeSpot(room, new Cell(1, 1));
                if (ok)
                {
                    return (new RoomUpgrade { Room = id, Slot = "plant_pot", Def = PlantPotDefinition, Anchor = cell, Rot = Rotation.North, Weakest = RoomStat.Beauty }, true);
                }
            }
            if (floorRooms.TryGetValue(id, out var fr) && fk)
            {
                var (u, ok) = FloorUpgrade(id, fr, fo!.Terrains, floors, gate, targets[id].Owners);
                if (ok)
                {
                    return (u, true);
                }
            }
        }
        return (new RoomUpgrade(), false);
    }

    /// <summary>Orders packed best first: quality, then market value, then id.</summary>
    internal static List<PackedSculpture> RankSculptures(IEnumerable<PackedSculpture> packed) =>
        packed.OrderByDescending(p => p.Quality).ThenByDescending(p => p.MarketValue).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The install due for the first (by room id) bedroom below target whose weakest stat is beauty, of the
    /// best packed sculpture (RankSculptures) whose footprint has a free spot there (a large sculpture needs a
    /// free multi-cell spot); the caller asks only once NextBeautyUpgrade has nothing.
    /// </summary>
    public static (SculptureStep Step, bool Ok) NextSculpture(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        IReadOnlyList<PackedSculpture> packed,
        ItemFacts items,
        RoomGate gate)
    {
        foreach (var room in SculptureRooms(obs, targets, rooms))
        {
            // The install is charged the sculpture's market value against the room's owners' share; the
            // rooms owed and the art sale are not gated.
            var affordable = packed.Where(p => gate.Allows(targets[room.Id].Owners, p.MarketValue)).ToList();
            var (p, cell, rot, ok) = FitSculpture(room.Room, RankSculptures(affordable), null, items);
            if (ok)
            {
                return (new SculptureStep { Room = room.Id, Packed = p.Id, Anchor = cell, Rot = rot }, true);
            }
        }
        return (new SculptureStep(), false);
    }

    /// <summary>The first of ranked, not in taken, with a free spot in room.</summary>
    internal static (PackedSculpture Sculpture, Cell Anchor, Rotation Rot, bool Ok) FitSculpture(
        FurnitureRoom room, IReadOnlyList<PackedSculpture> ranked, ISet<string>? taken, ItemFacts items)
    {
        foreach (var p in ranked)
        {
            var (size, ok) = items.SculptureSize(p.Def);
            if (!ok || (taken is not null && taken.Contains(p.Id)))
            {
                continue;
            }
            var (cell, rot, found) = FreeSpotFacing(room, size, Rotation.North);
            if (found)
            {
                return (p, cell, rot, true);
            }
        }
        return (new PackedSculpture(), default, Rotation.North, false);
    }

    /// <summary>
    /// The packed art the trade selector may sell: every piece but those NextSculpture would install, one per
    /// owed room, best first.
    /// </summary>
    public static HashSet<string> SaleSculptures(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        IReadOnlyList<PackedSculpture> packed,
        ItemFacts items)
    {
        var ranked = RankSculptures(packed);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in SculptureRooms(obs, targets, rooms))
        {
            var (p, _, _, ok) = FitSculpture(room.Room, ranked, reserved, items);
            if (ok)
            {
                reserved.Add(p.Id);
            }
        }
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in packed)
        {
            if (!reserved.Contains(p.Id))
            {
                output.Add(p.Id);
            }
        }
        return output;
    }

    /// <summary>The target rooms (by id) below target whose weakest stat is beauty.</summary>
    internal static List<string> BeautyRooms(SleepingObservation obs, IReadOnlyDictionary<string, RoomTarget> targets)
    {
        var output = new List<string>();
        if (!obs.Rooms.TryGetValue(out var census))
        {
            return output;
        }
        foreach (var r in census)
        {
            var tk = targets.TryGetValue(r.Id, out var t);
            var qk = r.Quality.TryGetValue(out var q);
            if (tk && qk && !t!.NeverUpgrade && t.Min > 0 && q!.Impressiveness < t.Min && (t.Max <= 0 || q.Impressiveness < t.Max) && WeakestRoomStat(q) == RoomStat.Beauty)
            {
                output.Add(r.Id);
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>Picks the most beautiful affordable floor and the room's cells whose terrain is less beautiful than it (none already ordered).</summary>
    internal static (RoomUpgrade Upgrade, bool Ok) FloorUpgrade(
        string id,
        FloorRoom room,
        IReadOnlyDictionary<string, FloorTerrain> terrains,
        FlooringFacts floors,
        RoomGate gate,
        IReadOnlyList<PawnId> owners)
    {
        var names = floors.Definitions.Keys.Order(StringComparer.Ordinal).ToList();
        var best = "";
        var bestBeauty = 0.0;
        foreach (var name in names)
        {
            var d = floors.Definitions[name];
            var ak = d.Available.TryGetValue(out var avail);
            var tk = d.Terrain.TryGetValue(out var terrain);
            var bk = d.Beauty.TryGetValue(out var beauty);
            if (!ak || !avail || !tk || !terrain || !bk || AffordableCells(d, floors.Stock, 1) == 0)
            {
                continue;
            }
            if (best.Length == 0 || beauty > bestBeauty)
            {
                best = name;
                bestBeauty = beauty;
            }
        }
        if (best.Length == 0)
        {
            return (new RoomUpgrade(), false);
        }
        var cells = new List<Cell>();
        foreach (var c in room.Cells)
        {
            if (c.Pending.Length != 0 || !terrains.TryGetValue(c.Terrain, out var t) || c.Terrain == best || t.Beauty >= bestBeauty)
            {
                continue;
            }
            cells.Add(c.Cell);
            if (cells.Count == MaxBeautyFloorCells)
            {
                break;
            }
        }
        // A gated room lays only the cells its owners' remaining share pays for, at the floor's material price.
        if (gate.Shares is not null && owners.Count > 0)
        {
            var (price, ok) = gate.FloorCellPrice(floors.Definitions[best]);
            if (!ok)
            {
                return (new RoomUpgrade(), false);
            }
            while (cells.Count > 0 && !gate.Allows(owners, price * cells.Count))
            {
                cells.RemoveAt(cells.Count - 1);
            }
        }
        if (cells.Count == 0)
        {
            return (new RoomUpgrade(), false);
        }
        return (new RoomUpgrade { Room = id, Slot = "floor", Def = best, Anchor = cells[0], Rot = Rotation.North, Cells = EquatableArray.From(cells), Weakest = RoomStat.Beauty }, true);
    }
}
