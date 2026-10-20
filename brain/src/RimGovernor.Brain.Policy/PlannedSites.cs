using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One hypothetical construction site: one owed operation of one planned room (Room, Op) in one stuff, or one
/// unplaced shelf candidate (Zone, Op Build, Room 0). Positions are its placement anchors, one per piece (every cell
/// of a wall run or floor op, a piece's one anchor). Material is the piece cost times the pieces, priced the way
/// PlannedOwedCost prices the op. The rest is the native planned-site answer (#3273) joined back by Id: CanPlace is
/// unknown while native has not answered (no read, an unknown read, an unresolved def) and Options are the
/// per pawn x material source rows.
/// A RoofIn site has no native placement: its work per cell is the coupled read's ThermalWorker.RoofCellWorkTicks,
/// so it is never requested and stays unknown here.
/// </summary>
public sealed record PlannedBuildSite(string Id, int Room, OpKind Op, string Definition, string Stuff, Rotation Rotation, EquatableArray<Cell> Positions)
{
    /// <summary>The store zone of a shelf candidate; empty for a room's operation.</summary>
    public string Zone { get; init; } = "";

    public EquatableArray<Amount> Material { get; init; }

    public Fact<bool> CanPlace { get; init; }

    public EquatableArray<ConstructionActivityOption> Options { get; init; }

    /// <summary>Feasibility once the footprint is cleared (native Verdict); Unspecified while native has not answered.</summary>
    public PlannedSiteVerdict Verdict { get; init; }

    /// <summary>The Cut, Haul and Mine prerequisites native reports for the footprint.</summary>
    public EquatableArray<PlannedSiteClearing> Clearing { get; init; }
}

/// <summary>
/// Every planned site of a round with the findings of building and reading them, and
/// the native routes between the sites of each room (#3313, PlannedRouteProbes): per pawn, from a site's location
/// (its first position) or a clearing's end to another site's location, a material source or a clearing's start,
/// and from each pawn's start to a roof.
/// </summary>
public sealed record PlannedSiteFacts
{
    public EquatableArray<PlannedBuildSite> Sites { get; init; }

    public EquatableArray<CoupledRoute> Routes { get; init; }

    public EquatableArray<string> Findings { get; init; }

    /// <summary>The layout plan's role of each room with sites (PlannedRoom.Role, #3651): the development count skips a priced role's sites.</summary>
    public EquatableArray<(int Room, PlannedRole Role)> Roles { get; init; }
}

/// <summary>A stuff a planned ring may be built from: its price (the cost list's market value) and whether the colony holds any.</summary>
public sealed record PlannedRingStuff(string Stuff, double Value, bool Stocked);

public static partial class PlannedRules
{
    /// <summary>The alternatives a planned room is compared on: every scope of the first stuff, then further stuffs.</summary>
    public const int PlannedAlternativesPerRoom = 4;

    /// <summary>Stuffs a ring is read in: the first takes the three scopes, each further stuff one alternative.</summary>
    public const int PlannedRingStuffCount = PlannedAlternativesPerRoom - 3 + 1;

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
    /// (floor), each piece one site in its own stuff (piece), the roof one site, and an owed wall swap (WallUp, #3657) one
    /// site per ring stuff over the standing walls <paramref name="wallStuff"/> names in another stuff (<c>wallup/{stuff}</c>:
    /// a wall upgrade, an alternative of the standing room the selection prices). A ring cell owed by two rooms
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
        IReadOnlySet<(string Def, Cell Cell)> placed,
        IReadOnlyDictionary<Cell, string> wallStuff)
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
                    case OpKind.WallUp:
                    {
                        // A standing wall swapped in place (#3657): per ring stuff, its walls built in another stuff.
                        var cells = Sorted(op.Cells.Where(c => claimed.Add((wallDef, c))));
                        foreach (var wanted in ringStuffs(room))
                        {
                            if (ring(wallDef, wanted) is { Stuff.Length: > 0 } priced)
                            {
                                var swapped = Sorted(cells.Where(c => wallStuff.TryGetValue(c, out var have) && have != priced.Stuff));
                                Add(new PlannedBuildSite(prefix + "/" + RingSiteKind(op.Kind) + "/" + priced.Stuff, room.Id, op.Kind, wallDef, priced.Stuff,
                                    Rotation.North, swapped) { Material = Priced(priced.Costs, swapped.Count) });
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
                sites.Add(new PlannedBuildSite(id, 0, OpKind.Build, StorageRules.ShelfDefinition, priced.Stuff, c.Piece.Rot, [anchor])
                    { Zone = c.Zone, Material = Priced(priced.Costs, 1) });
            }
        }
        return sites;
    }

    /// <summary>
    /// The native request for the round's sites: every site but a roof (no native placement), which stays unknown. One
    /// read answers them all (#3341), so a path the sites share is searched once.
    /// </summary>
    public static EquatableArray<PlannedConstructionSite> PlannedSiteRequest(IReadOnlyList<PlannedBuildSite> sites) =>
        EquatableArray.From(sites.Where(s => s.Op != OpKind.RoofIn).Select(s => new PlannedConstructionSite(s.Id, s.Definition, s.Stuff, s.Rotation, s.Positions)));

    /// <summary>
    /// The sites with the read's answers joined back by id. The read covers every free colonist (#3341), so an answer
    /// is the site's whole answer. A missing or unknown read (planned_site_read_unknown) answers none, and a site no
    /// answer names stays unknown. The read's own findings are kept. Null with nothing asked adds no finding.
    /// </summary>
    public static PlannedSiteFacts JoinPlannedSites(IReadOnlyList<PlannedBuildSite> sites, ConstructionActivityRead? read)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        var answers = new Dictionary<string, PlannedSiteAnswer>(StringComparer.Ordinal);
        if (read is { Known: true })
        {
            all.UnionWith(read.Findings);
            foreach (var answer in read.Planned)
            {
                answers[answer.Site] = answer;
            }
        }
        else if (sites.Any(s => s.Op != OpKind.RoofIn))
        {
            all.Add("planned_site_read_unknown");
        }
        var joined = sites.Select(site => answers.TryGetValue(site.Id, out var a)
            ? site with
            {
                CanPlace = a.CanPlace,
                Options = EquatableArray.From(a.Options.Where(o => o.Site == site.Id)),
                Verdict = a.Verdict,
                Clearing = a.Clearing,
            }
            : site);
        return new PlannedSiteFacts { Sites = EquatableArray.From(joined), Findings = EquatableArray.From(all) };
    }

    /// <summary>
    /// The route probes between each room's answered sites (#3313), all asked in one read (#3341). A room is probed when
    /// one of its sites has native rows; its probed sites are those with rows, plus its roof. Each route runs from
    /// where an activity of the room ends (a site's location, its first position; a clearing row's Location) to where
    /// another starts: a site's location (reaching its nearest position), a material source (a row's Origin), a
    /// clearing's start (a haul's Origin, a cut or mine row's Location). A roof is also probed from each pawn's start,
    /// since no native row routes to it.
    /// </summary>
    public static EquatableArray<PlannedRouteProbe> PlannedRouteProbes(IReadOnlyList<PlannedBuildSite> sites)
    {
        var probes = new List<PlannedRouteProbe>();
        foreach (var room in sites.Where(s => s.Room > 0 && s.Zone.Length == 0 && s.Positions.Count > 0).GroupBy(s => s.Room).OrderBy(g => g.Key))
        {
            var probed = room.Where(s => s.Op == OpKind.RoofIn || s.Options.Count > 0).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            if (!probed.Any(s => s.Op != OpKind.RoofIn))
            {
                continue;
            }
            var ends = new List<Cell>();
            var starts = new List<(Cell To, EquatableArray<Cell> Reach)>();
            foreach (var site in probed)
            {
                var at = site.Positions[0];
                ends.Add(at);
                starts.Add((at, site.Positions));
                if (site.Op == OpKind.RoofIn)
                {
                    probes.Add(new PlannedRouteProbe(at, at, site.Positions) { FromStart = true });
                }
                starts.AddRange(site.Options.Where(o => o.Count > 0).Select(o => (o.Origin, default(EquatableArray<Cell>))));
                foreach (var clearing in site.Clearing.OrderBy(c => c.Id, StringComparer.Ordinal))
                {
                    foreach (var row in clearing.Options)
                    {
                        starts.Add((clearing.Kind == PlannedClearingKind.Haul ? row.Origin : row.Location, default));
                        ends.Add(row.Location);
                    }
                }
            }
            var targets = starts.DistinctBy(s => s.To).ToArray();
            foreach (var from in ends.Distinct())
            {
                probes.AddRange(targets.Where(t => t.To != from).Select(t => new PlannedRouteProbe(from, t.To, t.Reach)));
            }
        }
        return EquatableArray.From(probes);
    }

    /// <summary>
    /// The facts with the route read's rows. A missing or unknown read (planned_route_read_unknown) contributes no
    /// route, so every route stays unknown; the read's own findings are kept. Null with nothing asked adds no finding.
    /// </summary>
    public static PlannedSiteFacts JoinPlannedRoutes(PlannedSiteFacts facts, EquatableArray<PlannedRouteProbe> asked, ConstructionActivityRead? read)
    {
        var all = new SortedSet<string>(facts.Findings, StringComparer.Ordinal);
        if (read is not { Known: true })
        {
            if (asked.Count > 0)
            {
                all.Add("planned_route_read_unknown");
            }
            return facts with { Routes = [], Findings = EquatableArray.From(all) };
        }
        all.UnionWith(read.Findings);
        return facts with { Routes = read.Routes, Findings = EquatableArray.From(all) };
    }

    private static string RingSiteKind(OpKind kind) => kind switch
    {
        OpKind.WallIn => "wall",
        OpKind.DoorIn => "door",
        OpKind.WallUp => "wallup",
        _ => "vent",
    };

    private static EquatableArray<Cell> Sorted(IEnumerable<Cell> cells) =>
        EquatableArray.From(cells.Distinct().OrderBy(c => c.X).ThenBy(c => c.Z));

    private static EquatableArray<Amount> Priced(IReadOnlyList<Amount> costs, long pieces) =>
        EquatableArray.From(costs.Where(c => c.Count > 0).GroupBy(c => c.Resource)
            .Select(g => new Amount(g.Key, g.Sum(c => c.Count) * pieces)).OrderBy(a => a.Resource.Value, StringComparer.Ordinal));
}
