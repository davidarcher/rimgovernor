namespace RimGovernor.Brain.Policy;

// Bed replacement: a solo bedroom below its RoomTarget whose owned bed is of poor quality (Awful or Poor)
// gets a new bed of the same definition. The game never places a bed over a standing one, so the replacement
// is one change walked across reviews, each step read back from the census rather than remembered:
//   - build: a PlaceBuilding for the new bed on free floor in the room;
//   - assign: once it stands, a AssignIntent moves the owner onto it;
//   - remove: the old bed, now unowned and worse, is deconstructed; so is a new bed that came out no
//     better (the next epoch rebuilds).
// Material: a room whose weakest stat is wealth or beauty gets its bed rebuilt in a better stuff the colony
// has stock for, judged by the stuff's beauty and market value factors (ItemFacts.StuffScore). A quality
// rebuild never builds in a worse stuff than the owned bed's.

/// <summary>
/// What a material choice reads: the colony stock by definition, the stuff units each bed definition takes
/// and the catalog's item facts, which rank the stuffs a definition accepts (ItemFacts.StuffScore: beauty
/// factor times market value). A stuff the catalog cannot score is never chosen.
/// </summary>
public sealed record BedMaterials
{
    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, long> Cost { get; init; } = new Dictionary<Resource, long>();

    public ItemFacts Items { get; init; } = new();

    /// <summary>The personal-share gate on a quality or material rebuild; the default is ungated.</summary>
    public RoomGate Gate { get; init; } = new();

    /// <summary>The best-scoring stocked stuff for def scoring above floor (or at least floor when orEqual), false when none.</summary>
    internal (Resource Stuff, bool Ok) BestStuff(Resource def, double floor, bool orEqual)
    {
        if (!Cost.TryGetValue(def, out var cost) || cost <= 0)
        {
            return (default, false);
        }
        Resource best = default;
        var bestScore = 0.0;
        foreach (var stuff in Items.StuffsFor(def))
        {
            if (!Items.TryStuffScore(stuff, out var score) || Stock.GetValueOrDefault(stuff) < cost || score < floor || (score == floor && !orEqual))
            {
                continue;
            }
            if (best.Value.Length == 0 || score > bestScore || (score == bestScore && stuff < best))
            {
                best = stuff;
                bestScore = score;
            }
        }
        return (best, best.Value.Length != 0);
    }
}

/// <summary>One step of a bed replacement.</summary>
public enum BedReplacementStep
{
    [WireName("")]
    None = 0,
    Build,
    Assign,
    Remove,
}

/// <summary>The next bed replacement step. Build places Def at Cell/Rot; Assign moves Pawn from PreviousBed to Bed; Remove deconstructs Bed (Def at Cell).</summary>
public sealed record BedReplacement
{
    public BedReplacementStep Step { get; init; }

    public string Room { get; init; } = "";

    public PawnId Pawn { get; init; }

    public string Bed { get; init; } = "";

    public string PreviousBed { get; init; } = "";

    public string Def { get; init; } = "";

    /// <summary>The material a Build step builds in, empty for the definition's default.</summary>
    public string Stuff { get; init; } = "";

    public Cell Cell { get; init; }

    public Rotation Rot { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Orders the native QualityCategory names.</summary>
    internal static readonly IReadOnlyDictionary<string, int> BedQualityRank = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Awful"] = 0, ["Poor"] = 1, ["Normal"] = 2, ["Good"] = 3, ["Excellent"] = 4, ["Masterwork"] = 5, ["Legendary"] = 6,
    };

    /// <summary>The North footprints of the beds the closer builds.</summary>
    internal static readonly IReadOnlyDictionary<Resource, Cell> ReplacementBedSizes = new Dictionary<Resource, Cell>
    {
        [new Resource("Bed")] = new Cell(1, 2),
        [new Resource("DoubleBed")] = new Cell(2, 2),
        [new Resource("RoyalBed")] = new Cell(2, 2),
    };

    /// <summary>
    /// Whether x improves on b for a room that wants the definition want: the wanted definition first, then
    /// quality and stuff, each no worse and one strictly better. A stuff unknown on either bed compares equal.
    /// </summary>
    internal static bool BedBetter(ItemFacts items, SleepingBed x, SleepingBed b, Resource want)
    {
        if (x.Definition != want)
        {
            return false;
        }
        if (b.Definition != want)
        {
            return true;
        }
        var xk = x.Quality.TryGetValue(out var xq);
        var bk = b.Quality.TryGetValue(out var bq);
        var xok = BedQualityRank.TryGetValue(xq ?? "", out var xr);
        var bok = BedQualityRank.TryGetValue(bq ?? "", out var br);
        if (!xk || !bk || !xok || !bok)
        {
            return false;
        }
        double xs = BedScore(items, x), bs = BedScore(items, b);
        xok = xs >= 0;
        bok = bs >= 0;
        if (!xok || !bok)
        {
            xs = 0;
            bs = 0;
        }
        return xr >= br && xs >= bs && (xr > br || xs > bs);
    }

    /// <summary>The bed's stuff score, -1 when unknown.</summary>
    internal static double BedScore(ItemFacts items, SleepingBed b)
    {
        if (!b.Stuff.TryGetValue(out var stuff))
        {
            return -1;
        }
        // A stuff the catalog does not know, or a frame without a catalog, is an unknown score: such a bed
        // is never judged better or worse by its stuff.
        return items.TryStuffScore(new Resource(stuff), out var score) ? score : -1;
    }

    /// <summary>
    /// The first (by room id) bed replacement step due, false when none. rooms are the furniture rooms;
    /// available reports a definition the colony can build now.
    /// </summary>
    public static (BedReplacement Step, bool Ok) NextBedReplacement(
        SleepingObservation obs,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<FurnitureRoom> rooms,
        Func<string, bool> available,
        BedMaterials materials)
    {
        if (!obs.Rooms.TryGetValue(out var census))
        {
            return (new BedReplacement(), false);
        }
        var quality = new Dictionary<string, RoomQuality>(StringComparer.Ordinal);
        foreach (var r in census)
        {
            if (r.Quality.TryGetValue(out var rq))
            {
                quality[r.Id] = rq;
            }
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        var inRoom = new Dictionary<string, List<SleepingBed>>(StringComparer.Ordinal);
        foreach (var bed in obs.Beds)
        {
            beds[bed.Id] = bed;
            var ok = bed.Room.TryGetValue(out var bedRoom);
            bed.Humanlike.TryGetValue(out var humanlike);
            bed.Medical.TryGetValue(out var medical);
            bed.Prisoners.TryGetValue(out var prisoners);
            if (ok && !string.IsNullOrEmpty(bedRoom) && humanlike && !medical && !prisoners && !bed.Slaves)
            {
                if (!inRoom.TryGetValue(bedRoom, out var list))
                {
                    inRoom[bedRoom] = list = [];
                }
                list.Add(bed);
            }
        }
        var titles = new Dictionary<PawnId, RoyalTitle?>();
        foreach (var p in obs.People)
        {
            titles[p.Id] = p.Title;
        }
        foreach (var s in SoloBedrooms(obs))
        {
            var owned = beds.GetValueOrDefault(s.Bed) ?? new SleepingBed();
            var want = owned.Definition;
            var (w0, wok) = TitleBed(titles.GetValueOrDefault(s.Owner), owned.Definition, available);
            if (wok)
            {
                want = w0;
            }
            var spare = new List<SleepingBed>();
            foreach (var b in inRoom.GetValueOrDefault(s.Room) ?? [])
            {
                if (b.Id != owned.Id && b.Owners.Count == 0)
                {
                    spare.Add(b);
                }
            }
            foreach (var b in spare)
            {
                if (BedBetter(materials.Items, b, owned, want))
                {
                    return (new BedReplacement { Step = BedReplacementStep.Assign, Room = s.Room, Pawn = s.Owner, Bed = b.Id, PreviousBed = owned.Id }, true);
                }
            }
            foreach (var b in spare)
            {
                if (!BedBetter(materials.Items, b, owned, want))
                {
                    return (new BedReplacement { Step = BedReplacementStep.Remove, Room = s.Room, Bed = b.Id, Def = b.Definition.Value, Cell = b.Cell }, true);
                }
            }
            var tk = targets.TryGetValue(s.Room, out var t);
            var qk = quality.TryGetValue(s.Room, out var q);
            var rk = furniture.TryGetValue(s.Room, out var room);
            if (spare.Count > 0 || !rk)
            {
                continue;
            }
            if (want != owned.Definition)
            {
                // A royal title's bed requirement holds regardless of the target.
                if (BedSpotCore(room!, want) is { } spot0)
                {
                    return (new BedReplacement { Step = BedReplacementStep.Build, Room = s.Room, Def = want.Value, Cell = spot0.Cell, Rot = spot0.Rotation }, true);
                }
                continue;
            }
            // A room short of space is its owner's suite claim, not a bigger bed's.
            if (!tk || !qk || t!.NeverUpgrade || t.Min <= 0 || q!.Impressiveness >= t.Min || (t.Max > 0 && q.Impressiveness >= t.Max) || WeakestRoomStat(q) == RoomStat.Space)
            {
                continue;
            }
            if (!available(want.Value))
            {
                continue;
            }
            var owns = BedScore(materials.Items, owned);
            owned.Quality.TryGetValue(out var name);
            Resource stuff = default;
            var weakest = WeakestRoomStat(q);
            if (BedQualityRank.TryGetValue(name ?? "", out var rank) && rank < BedQualityRank["Normal"])
            {
                // Quality: never in a worse stuff than the owned bed's.
                if (owns >= 0)
                {
                    var (st, sok) = materials.BestStuff(want, owns, true);
                    if (!sok)
                    {
                        continue;
                    }
                    stuff = st;
                }
            }
            else if (owns >= 0 && (weakest == RoomStat.Wealth || weakest == RoomStat.Beauty))
            {
                var (st, sok) = materials.BestStuff(want, owns, false);
                if (!sok)
                {
                    continue;
                }
                stuff = st;
            }
            else
            {
                continue;
            }
            if (!materials.Gate.BedAllowed([s.Owner], want, stuff, owned))
            {
                continue;
            }
            if (BedSpotCore(room!, want) is { } spot)
            {
                return (new BedReplacement { Step = BedReplacementStep.Build, Room = s.Room, Def = want.Value, Stuff = stuff.Value, Cell = spot.Cell, Rot = spot.Rotation }, true);
            }
        }
        return (new BedReplacement(), false);
    }

    /// <summary>The first free anchor (back row first) where a bed of def fits the room's floor clear of its furniture and keeps it walkable.</summary>
    internal static (Cell Cell, Rotation Rotation)? BedSpotCore(FurnitureRoom room, Resource def)
    {
        if (!ReplacementBedSizes.TryGetValue(def, out var size))
        {
            return null;
        }
        var (anchor, rot, ok) = FreeSpot(room, size);
        return ok ? (anchor, rot) : null;
    }

    /// <summary>The first free anchor (back row first) where a piece of the North size fits the room's floor clear of its furniture and keeps it walkable.</summary>
    internal static (Cell Anchor, Rotation Rot, bool Ok) FreeSpot(FurnitureRoom room, Cell size) =>
        FreeSpotFacing(room, size, Rotation.South, Rotation.North);

    /// <summary>FreeSpot over the given rotations only: a sculpture is not rotatable, so it faces North.</summary>
    internal static (Cell Anchor, Rotation Rot, bool Ok) FreeSpotFacing(FurnitureRoom room, Cell size, params Rotation[] rots)
    {
        var inner = room.Room.Interior;
        var blocked = new HashSet<Cell>();
        foreach (var p in room.Pieces)
        {
            foreach (var c in RectCells(p.Rect))
            {
                blocked.Add(c);
            }
        }
        for (var z = inner.Z + inner.Height - 1; z >= inner.Z; z--)
        {
            for (var x = inner.X; x < inner.X + inner.Width; x++)
            {
                foreach (var rot in rots)
                {
                    var anchor = new Cell(x, z);
                    var r = OccupiedRect(anchor, size, rot);
                    if (r.Width == 0 || r.X < inner.X || r.Z < inner.Z || r.X + r.Width > inner.X + inner.Width || r.Z + r.Height > inner.Z + inner.Height)
                    {
                        continue;
                    }
                    var cells = RectCells(r);
                    var free = true;
                    foreach (var c in cells)
                    {
                        free = free && !blocked.Contains(c);
                    }
                    if (free && InteriorPlacementWalkable(room.Room, blocked, cells))
                    {
                        return (anchor, rot, true);
                    }
                }
            }
        }
        return (default, Rotation.South, false);
    }

    /// <summary>The bed a royal title requires when the owned bed does not meet it: the first buildable bed of the title's bed entry.</summary>
    internal static (Resource Def, bool Ok) TitleBed(RoyalTitle? title, Resource owned, Func<string, bool> available)
    {
        if (title is null)
        {
            return (default, false);
        }
        foreach (var thing in title.BedroomThings)
        {
            if (!IsReplacementBed(thing.AnyOf))
            {
                continue;
            }
            foreach (var d in thing.AnyOf)
            {
                if (d == owned)
                {
                    return (default, false);
                }
            }
            foreach (var d in thing.AnyOf)
            {
                if (ReplacementBedSizes.ContainsKey(d) && available(d.Value))
                {
                    return (d, true);
                }
            }
        }
        return (default, false);
    }

    internal static bool IsReplacementBed(IReadOnlyList<Resource> defs)
    {
        foreach (var d in defs)
        {
            if (ReplacementBedSizes.ContainsKey(d))
            {
                return true;
            }
        }
        return false;
    }
}
