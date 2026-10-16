using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One hypothetical construction site: one owed operation of one planned room (Room, Op) in one stuff, or one
/// unplaced shelf candidate (Zone, Op Build, Room 0). Positions are its placement anchors, one per piece (every cell
/// of a wall run or floor op, a piece's one anchor). Material is the piece cost times the pieces, priced the way
/// PlannedOwedCost prices the op. The rest is the native planned-site answer (#3273) joined back by Id: CanPlace is
/// unknown while native has not answered (no read, a truncated or unknown read, an unresolved def), Options are the
/// per pawn x material source rows, and Blockers are the footprint's things unclassified, as native reports them.
/// A RoofIn site has no native placement: its work per cell is the coupled read's ThermalWorker.RoofCellWorkTicks,
/// so it is never requested and stays unknown here.
/// </summary>
public sealed record PlannedBuildSite(string Id, int Room, OpKind Op, string Definition, string Stuff, Rotation Rotation, EquatableArray<Cell> Positions)
{
    /// <summary>The store zone of a shelf candidate; empty for a room's operation.</summary>
    public string Zone { get; init; } = "";

    public EquatableArray<Amount> Material { get; init; }

    public Fact<bool> CanPlace { get; init; }

    public EquatableArray<PlannedSiteRejection> Rejections { get; init; }

    public EquatableArray<PlannedSiteBlocker> Blockers { get; init; }

    public EquatableArray<ConstructionActivityOption> Options { get; init; }

    /// <summary>Feasibility once the footprint is cleared (native Verdict); Unspecified while native has not answered.</summary>
    public PlannedSiteVerdict Verdict { get; init; }

    /// <summary>The Cut, Haul and Mine prerequisites native reports for the footprint.</summary>
    public EquatableArray<PlannedSiteClearing> Clearing { get; init; }
}

/// <summary>Every planned site of a round with the findings of building and reading them (a truncated read among them).</summary>
public sealed record PlannedSiteFacts
{
    public EquatableArray<PlannedBuildSite> Sites { get; init; }

    public EquatableArray<string> Findings { get; init; }
}

/// <summary>A stuff a planned ring may be built from: its price (the cost list's market value) and whether the colony holds any.</summary>
public sealed record PlannedRingStuff(string Stuff, double Value, bool Stocked);

public static partial class PolicyFunctions
{
    /// <summary>The alternatives a planned room is compared on: every scope of the first stuff, then further stuffs.</summary>
    public const int PlannedAlternativesPerRoom = 4;

    /// <summary>Stuffs a ring is read in: the first takes the three scopes, each further stuff one alternative.</summary>
    public const int PlannedRingStuffCount = PlannedAlternativesPerRoom - 3 + 1;

    /// <summary>Planned sites one construction activity read carries: few, so the read's shared 128-row budget covers each.</summary>
    public const int PlannedSitesPerRead = 4;

    /// <summary>The native read's per-site position cap: a longer site is not asked for and stays unknown.</summary>
    public const int PlannedSitePositionCap = 128;

    /// <summary>
    /// The stuffs a planned ring is read in, cheapest available first (PlannedRingStuffCount of them): the stocked
    /// stuffs by price, then name. With none stocked, the cheapest stuff the ring waits for. A ring def made from no
    /// stuff (options empty) is read once, with the empty stuff.
    /// </summary>
    public static IReadOnlyList<string> PlannedRingStuffs(IReadOnlyList<PlannedRingStuff> options)
    {
        if (options.Count == 0)
        {
            return [""];
        }
        var ranked = options.OrderBy(o => o.Value).ThenBy(o => o.Stuff, StringComparer.Ordinal).ToList();
        var stocked = ranked.Where(o => o.Stocked).Select(o => o.Stuff).Take(PlannedRingStuffCount).ToList();
        return stocked.Count > 0 ? stocked : [ranked[0].Stuff];
    }

    /// <summary>
    /// The planned sites of the rooms' owed operations, one per op x stuff: the ring's walls, doors and vents in each
    /// of the room's ring stuffs (ringStuffs; ring gives the stuff a def is built from for a wanted stuff, "" for a def
    /// made from no stuff, and its cost list, or null where the def is unavailable), floors one site per floor def
    /// (floor), each piece one site in its own stuff (piece), and the roof one site. A ring cell owed by two rooms
    /// belongs to the lower room id, and a ring cell or piece with a blueprint or frame placed is left out, as
    /// PlannedOwedCost counts them. Nothing is read: every site is unknown until JoinPlannedSites.
    /// </summary>
    public static IReadOnlyList<PlannedBuildSite> PlannedSitesOf(
        IEnumerable<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> rooms,
        Func<PlannedRoom, IReadOnlyList<string>> ringStuffs,
        Func<string, string, (string Stuff, IReadOnlyList<Amount> Costs)?> ring,
        Func<string, (string Stuff, IReadOnlyList<Amount> Costs)?> piece,
        Func<string, IReadOnlyList<Amount>?> floor,
        string ventDef,
        IReadOnlySet<(string Def, Cell Cell)> placed)
    {
        var claimed = new HashSet<(string Def, Cell Cell)>(placed);
        var roofed = new HashSet<Cell>();
        var sites = new List<PlannedBuildSite>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(PlannedBuildSite site)
        {
            if (site.Positions.Count > 0 && ids.Add(site.Id))
            {
                sites.Add(site);
            }
        }
        foreach (var (room, owed) in rooms.OrderBy(r => r.Room.Id))
        {
            var (wallDef, doorDef) = room.RingDefs();
            var prefix = "planned/room" + room.Id;
            foreach (var op in owed)
            {
                switch (op.Kind)
                {
                    case OpKind.WallIn:
                    case OpKind.DoorIn:
                    case OpKind.VentIn:
                    {
                        var def = op.Kind == OpKind.WallIn ? wallDef : op.Kind == OpKind.DoorIn ? doorDef : ventDef;
                        var cells = Sorted(op.Cells.Where(c => claimed.Add((def, c))));
                        foreach (var wanted in ringStuffs(room))
                        {
                            if (ring(def, wanted) is { } priced)
                            {
                                Add(new PlannedBuildSite(prefix + "/" + RingSiteKind(op.Kind) + (priced.Stuff.Length > 0 ? "/" + priced.Stuff : ""),
                                    room.Id, op.Kind, def, priced.Stuff, Rotation.North, cells) { Material = Priced(priced.Costs, cells.Count) });
                            }
                        }
                        break;
                    }
                    case OpKind.FloorIn:
                        foreach (var group in op.Floors.GroupBy(f => f.DefName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
                        {
                            if (floor(group.Key) is { } costs)
                            {
                                var cells = Sorted(group.Select(f => f.Cell));
                                Add(new PlannedBuildSite(prefix + "/floor/" + group.Key, room.Id, op.Kind, group.Key, "", Rotation.North, cells)
                                    { Material = Priced(costs, cells.Count) });
                            }
                        }
                        break;
                    case OpKind.Build:
                        foreach (var p in op.Pieces)
                        {
                            var anchor = p.Anchor();
                            if (!placed.Contains((p.DefName, anchor)) && piece(p.DefName) is { } priced)
                            {
                                Add(new PlannedBuildSite(prefix + "/build/" + p.DefName + "/" + anchor.X + "/" + anchor.Z, room.Id, op.Kind, p.DefName,
                                    priced.Stuff, p.Rot, [anchor]) { Material = Priced(priced.Costs, 1) });
                            }
                        }
                        break;
                    case OpKind.RoofIn:
                        Add(new PlannedBuildSite(prefix + "/roof", room.Id, op.Kind, "", "", Rotation.North, Sorted(op.Cells.Where(roofed.Add))));
                        break;
                }
            }
        }
        return sites;
    }

    /// <summary>
    /// The ground as if the room's owed ring work were done (#3276): a copy of ground with each owed WallIn and DoorOut
    /// cell walled, each DoorIn cell a door and each WallOut cell cleared. Reconcile over it owes the roof and the vents
    /// the ring brings, by the same rule real placement meets once the ring stands; nothing else is assumed built.
    /// </summary>
    public static GroundCensus RingStandsGround(GroundCensus ground, IReadOnlyList<Operation> owed)
    {
        var g = new GroundCensus
        {
            Walls = [.. ground.Walls],
            Doors = [.. ground.Doors],
            Fences = [.. ground.Fences],
            Gates = [.. ground.Gates],
            Stuff = new Dictionary<Cell, string>(ground.Stuff),
            Vents = [.. ground.Vents],
            Rock = [.. ground.Rock],
        };
        foreach (var op in owed)
        {
            foreach (var c in op.Cells)
            {
                switch (op.Kind)
                {
                    case OpKind.WallIn:
                    case OpKind.DoorOut:
                        _ = g.Doors.Remove(c);
                        _ = g.Walls.Add(c);
                        break;
                    case OpKind.DoorIn:
                        _ = g.Walls.Remove(c);
                        _ = g.Doors.Add(c);
                        break;
                    case OpKind.WallOut:
                        _ = g.Walls.Remove(c);
                        break;
                }
            }
        }
        return g;
    }

    /// <summary>
    /// The operations a room's planned sites are built from: what Reconcile owes now (owed) without its vents and roof,
    /// plus the vents and roof it owes once the ring stands (stands, Reconcile over RingStandsGround). A standing ring
    /// owes the same either way. PlannedOwedCost keeps pricing owed itself.
    /// </summary>
    public static IReadOnlyList<Operation> PlannedSitedOps(IReadOnlyList<Operation> owed, IReadOnlyList<Operation> stands) =>
        [.. owed.Where(o => o.Kind is not (OpKind.VentIn or OpKind.RoofIn)), .. stands.Where(o => o.Kind is OpKind.VentIn or OpKind.RoofIn)];

    /// <summary>Each unplaced shelf candidate as a one-piece planned site in the shelf's stuff and price (shelf; null leaves none).</summary>
    public static IReadOnlyList<PlannedBuildSite> ShelfPlannedSites(IEnumerable<ShelfCandidate> candidates, (string Stuff, IReadOnlyList<Amount> Costs)? shelf)
    {
        if (shelf is not { } priced)
        {
            return [];
        }
        var sites = new List<PlannedBuildSite>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var anchor = c.Piece.Wanted().Anchor();
            var id = "planned/shelf/" + c.Zone + "/" + anchor.X + "/" + anchor.Z;
            if (ids.Add(id))
            {
                sites.Add(new PlannedBuildSite(id, 0, OpKind.Build, ShelfDefinition, priced.Stuff, c.Piece.Rot, [anchor])
                    { Zone = c.Zone, Material = Priced(priced.Costs, 1) });
            }
        }
        return sites;
    }

    /// <summary>
    /// The native requests for sites, PlannedSitesPerRead to a read. A roof (no native placement) is not asked for, nor
    /// is a site over the read's position cap (finding planned_site_positions_exceeded): both stay unknown.
    /// </summary>
    public static (IReadOnlyList<EquatableArray<PlannedConstructionSite>> Batches, IReadOnlyList<string> Findings) PlannedSiteBatches(IReadOnlyList<PlannedBuildSite> sites)
    {
        var findings = new List<string>();
        var asked = new List<PlannedConstructionSite>();
        foreach (var site in sites)
        {
            if (site.Op == OpKind.RoofIn)
            {
                continue;
            }
            if (site.Positions.Count > PlannedSitePositionCap)
            {
                findings.Add("planned_site_positions_exceeded");
                continue;
            }
            asked.Add(new PlannedConstructionSite(site.Id, site.Definition, site.Stuff, site.Rotation, site.Positions));
        }
        return ([.. asked.Chunk(PlannedSitesPerRead).Select(c => EquatableArray.From(c))], findings);
    }

    /// <summary>
    /// The sites with each read's answers joined back by id. A read that is missing or unknown (planned_site_read_unknown)
    /// or truncated (planned_site_read_truncated: its row budget may have cut any of its sites' rows) answers none of its
    /// sites, so they stay unknown; a site no answer names stays unknown. The reads' own findings are kept.
    /// </summary>
    public static PlannedSiteFacts JoinPlannedSites(IReadOnlyList<PlannedBuildSite> sites, IEnumerable<string> findings,
        IEnumerable<(EquatableArray<PlannedConstructionSite> Asked, ConstructionActivityRead? Read)> reads)
    {
        var all = new SortedSet<string>(findings, StringComparer.Ordinal);
        var answers = new Dictionary<string, PlannedSiteAnswer>(StringComparer.Ordinal);
        foreach (var (asked, read) in reads)
        {
            if (read is not { Known: true })
            {
                all.Add("planned_site_read_unknown");
                continue;
            }
            all.UnionWith(read.Findings);
            if (read.Truncated)
            {
                all.Add("planned_site_read_truncated");
                continue;
            }
            var names = asked.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var answer in read.Planned.Where(a => names.Contains(a.Site)))
            {
                answers[answer.Site] = answer;
            }
        }
        var joined = sites.Select(site => answers.TryGetValue(site.Id, out var a)
            ? site with
            {
                CanPlace = a.CanPlace,
                Rejections = a.Rejections,
                Blockers = a.Blockers,
                Options = EquatableArray.From(a.Options.Where(o => o.Site == site.Id)),
                Verdict = a.Verdict,
                Clearing = a.Clearing,
            }
            : site);
        return new PlannedSiteFacts { Sites = EquatableArray.From(joined), Findings = EquatableArray.From(all) };
    }

    private static string RingSiteKind(OpKind kind) => kind switch
    {
        OpKind.WallIn => "wall",
        OpKind.DoorIn => "door",
        _ => "vent",
    };

    private static EquatableArray<Cell> Sorted(IEnumerable<Cell> cells) =>
        EquatableArray.From(cells.Distinct().OrderBy(c => c.X).ThenBy(c => c.Z));

    private static EquatableArray<Amount> Priced(IReadOnlyList<Amount> costs, long pieces) =>
        EquatableArray.From(costs.Where(c => c.Count > 0).GroupBy(c => c.Resource)
            .Select(g => new Amount(g.Key, g.Sum(c => c.Count) * pieces)).OrderBy(a => a.Resource.Value, StringComparer.Ordinal));
}
