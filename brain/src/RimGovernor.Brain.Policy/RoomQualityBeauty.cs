namespace RimGovernor.Brain.Policy;

// Beauty levers: an owned bedroom below its target whose weakest stat is beauty gets, one change at a time,
//   - a plant pot on free floor (native growers sow its default plant);
//   - a prettier floor: every cell whose terrain is less beautiful than the most beautiful affordable floor
//     the colony can lay, up to one plan's worth of cells.
// The template furniture (NextRoomUpgrade) and bed (NextBedReplacement) levers go first; these only add beauty.

/// <summary>Installs Packed (a packed item's id) at Anchor, Rot.</summary>
public sealed record SculptureStep
{
    public string Packed { get; init; } = "";

    public Cell Anchor { get; init; }

    public Rotation Rot { get; init; }
}

/// <summary>A packed sculpture in stock: its id, the sculpture definition inside, its quality (QualityCategory ordinal, -1 unknown) and market value.</summary>
public sealed record PackedSculpture
{
    public required RimGovernor.Brain.GameFacts.Observations.StockItem Observed { get; init; }
    public RimGovernor.Brain.GameFacts.Observations.PackedBuilding Packed => Observed.Packed
        ?? throw new DomainException("packed sculpture lacks its building");
    public string Id => Observed.Id;
    public string Def => Packed.DefName;
    public int Quality => Packed.Quality is RimGovernor.Brain.GameFacts.Observations.PackedQuality.Rated rated ? (int)rated.Category : -1;
    public double MarketValue => Packed.MarketValue;
}

public static partial class RoomQualityRules
{
    /// <summary>The plant pot the beauty lever places.</summary>
    public const string PlantPotDefinition = "PlantPot";

    /// <summary>Bounds one floor upgrade plan, as the rooms/flooring Need's MaxCellsPerPlan does.</summary>
    internal const int MaxBeautyFloorCells = 24;

    /// <summary>
    /// The first (by room id) beauty upgrade due, false when none. A floor upgrade carries its cells in Cells
    /// (Def placed at each, North); a plant pot is one piece at Anchor.
    /// </summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextBeautyUpgrade(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        Func<string, bool> available,
        FlooringObservation flooring,
        FlooringFacts floors)
    {
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        var floorRooms = new Dictionary<string, FloorRoom>(StringComparer.Ordinal);
        foreach (var r in flooring.Rooms)
        {
            floorRooms[r.Id] = r;
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
            if (!pot && available(PlantPotDefinition))
            {
                var (cell, _, ok) = FreeSpot(room, new Cell(1, 1));
                if (ok)
                {
                    return (new RoomUpgrade { Room = id, Slot = "plant_pot", Def = PlantPotDefinition, Size = new Cell(1, 1), Anchor = cell, Rot = Rotation.North }, true);
                }
            }
            if (floorRooms.TryGetValue(id, out var fr))
            {
                var (u, ok) = FloorUpgrade(id, fr, flooring.Terrains, floors);
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
        ItemFacts items)
    {
        foreach (var room in ArtRules.SculptureRooms(obs, targets, rooms))
        {
            var (p, cell, rot, ok) = FitSculpture(room.Room, RankSculptures(packed), null, items);
            if (ok && p is not null)
            {
                return (new SculptureStep { Packed = p.Id, Anchor = cell, Rot = rot }, true);
            }
        }
        return (new SculptureStep(), false);
    }

    /// <summary>The first of ranked, not in taken, with a free spot in room.</summary>
    internal static (PackedSculpture? Sculpture, Cell Anchor, Rotation Rot, bool Ok) FitSculpture(
        FurnitureRoom room, IReadOnlyList<PackedSculpture> ranked, ISet<string>? taken, ItemFacts items)
    {
        foreach (var p in ranked)
        {
            var ok = items.SculptureSize(p.Def, out var size);
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
        return (null, default, Rotation.North, false);
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
        foreach (var room in ArtRules.SculptureRooms(obs, targets, rooms))
        {
            var (p, _, _, ok) = FitSculpture(room.Room, ranked, reserved, items);
            if (ok && p is not null)
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
        var census = obs.Rooms;
        foreach (var r in census)
        {
            var q = r.Quality;
            if (targets.TryGetValue(r.Id, out var t) && !t!.NeverUpgrade && t.Min > 0 && q!.Impressiveness < t.Min && (t.Max <= 0 || q.Impressiveness < t.Max) && WeakestRoomStat(q) == RoomStat.Beauty)
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
        FlooringFacts floors)
    {
        var names = floors.Definitions.Keys.Order(StringComparer.Ordinal).ToList();
        var best = "";
        var bestBeauty = 0.0;
        foreach (var name in names)
        {
            var d = floors.Definitions[name];
            var avail = d.Available;
            var terrain = d.Terrain;
            var beauty = d.Beauty;
            if (!avail || !terrain || FlooringRules.AffordableCells(d, floors.Stock, 1) == 0)
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
            if (c.Pending is not null || !terrains.TryGetValue(c.Terrain, out var t) || c.Terrain == best || t.Beauty >= bestBeauty)
            {
                continue;
            }
            cells.Add(c.Cell);
            if (cells.Count == MaxBeautyFloorCells)
            {
                break;
            }
        }
        if (cells.Count == 0)
        {
            return (new RoomUpgrade(), false);
        }
        return (new RoomUpgrade { Room = id, Slot = "floor", Def = best, Anchor = cells[0], Rot = Rotation.North, Cells = EquatableArray.From(cells) }, true);
    }
}
