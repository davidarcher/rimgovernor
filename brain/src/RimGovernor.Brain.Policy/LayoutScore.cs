namespace RimGovernor.Brain.Policy;

/// <summary>The one weight table of plan scoring. The replay harness tunes it.</summary>
public static class PlanWeights
{
    // Hard tier, charged once per failure so failing plans still rank (more missing rooms lower) inside their tier.
    public const int MissingRoom = 100000;
    public const int BadRoutes = 100000;

    // Soil: per soilCost point under a room, and per rock cell (a dig cost). Rock is a dig, but its walls and floor come with it: a little dearer than the plain soil a built room covers.
    public const int Soil = 3;
    public const int Rock = 8;

    // Footprint per cell the ring would enclose; Wall per built wall cell.
    public const int Footprint = 1;
    public const int Wall = 10;

    // Edge: per step a room cell stands inside EdgeClear walking distance of an open map edge cell (raiders arrive there).
    public const int Edge = 3;
    public const int EdgeClear = 50;

    // Centre: per step a room cell stands inside CentreClear cells of the map border, rock or not. A mountain on the border saves a long wall and a dig's worth of soil, thousands of points; a room cell pays Centre per step inside a third of the map's short side, so tens of cells off the border outweigh it.
    public const int Centre = 1;
    public const int CentreClear = 83;

    // Defense: a killbox placed, an approach lane placed, and per ring cell nothing can close.
    public const int Killbox = 200;
    public const int Approach = 100;
    public const int Gap = 20;

    // Walk: per step of planWalk, the affinity-weighted walking distance between the rooms pawns travel.
    public const int Walk = 1;

    // Expansion: per free core cell within ExpansionReach of the plan, at most ExpansionCap cells counted.
    public const int Expansion = 1;
    public const int ExpansionReach = 6;
    public const int ExpansionCap = 300;

    /// <summary>The score a replan's candidate must beat the saved plan's by before the unbuilt rooms are sited again: about a room's worth of plain soil, so near-equal layouts never flicker.</summary>
    public const int ReplanGain = 100;

    /// <summary>The score a plan built on field ground must beat the best plan off it by before siting uses it.</summary>
    public const int FieldGain = 2000;

    /// <summary>BuiltSwitch and BuiltSwitchCell price moving a room that stands: what a relocation must gain beyond ReplanGain, per room and per floor cell (the walls, floor and furniture built twice).</summary>
    public const int BuiltSwitch = 200;

    public const int BuiltSwitchCell = 4;
}

/// <summary>A plan's per-term values and tier verdict. The soft terms are signed contributions to Total (costs negative).</summary>
public sealed record PlanScore
{
    /// <summary>Hard tier: base rooms the plan lacks.</summary>
    public IReadOnlyList<PlannedRole> Missing { get; init; } = [];

    /// <summary>CheckRoutes' error, empty when the routes are valid.</summary>
    public string RoutesErr { get; init; } = "";

    /// <summary>Rich-soil cells under rooms.</summary>
    public int RichCells { get; init; }

    public int Soil { get; init; }
    public int Footprint { get; init; }
    public int Wall { get; init; }
    public int Edge { get; init; }
    public int Centre { get; init; }
    public int Defense { get; init; }
    public int Expansion { get; init; }
    public int Walk { get; init; }

    /// <summary>Whether Wall and Defense were scored (PlanPerimeter ran); a cheap score leaves them zero.</summary>
    public bool Walled { get; init; }

    /// <summary>The hard-tier verdict.</summary>
    public bool Passes() => Missing.Count == 0 && RoutesErr == "" && RichCells == 0;

    /// <summary>The soft terms plus the hard-tier failure charges.</summary>
    public int Total()
    {
        var t = Soil + Footprint + Wall + Edge + Centre + Defense + Expansion + Walk;
        t -= PlanWeights.MissingRoom * Missing.Count;
        if (RoutesErr != "")
        {
            t -= PlanWeights.BadRoutes;
        }
        return t;
    }

    /// <summary>Whether this ranks above o: a passing plan above a failing one, then the higher Total.</summary>
    public bool Better(PlanScore o)
    {
        bool a = Passes(), b = o.Passes();
        if (a != b)
        {
            return a;
        }
        return Total() > o.Total();
    }

    public override string ToString() =>
        $"pass={(Passes() ? "true" : "false")} total={Total()} missing={Missing.Count} routes=\"{RoutesErr.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\" rich={RichCells} soil={Soil} footprint={Footprint} wall={Wall} edge={Edge} centre={Centre} defense={Defense} expansion={Expansion} walk={Walk}";
}

/// <summary>The per-survey state every candidate's score shares.</summary>
internal sealed record PlanScorer(BuildGround G, MapSurvey S, SiteGround Ground)
{
    /// <summary>Go <c>newPlanScorer</c>: the scorer over a survey, its zones and the ground already reserved.</summary>
    public PlanScorer(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutReservation> reserved, MapSurvey s)
        : this(BuildGround.Create(zones, reserved).WithSoil(s), s, PolicyFunctions.NewSiteGround(s))
    {
    }

    /// <summary>Scores every term that needs no wall: the hard tier, soil and rock, walking distance, footprint, the map edge and expansion room.</summary>
    public PlanScore Core(LayoutPlan p)
    {
        var output = new PlanScore();

        // Attached children (an airlock, a porch) are no rooms of their own to owe, site or weigh: their parent stands for them.
        var rooms = p.Rooms.Where(r => !r.Attached()).ToList();
        output = output with { Missing = PolicyFunctions.MissingRooms(rooms, p.Owed) };
        if (rooms.Count == 0)
        {
            return output;
        }
        var routes = PolicyFunctions.CheckRoutes(p);
        if (routes.Error is { } err)
        {
            output = output with { RoutesErr = err };
        }

        var under = new HashSet<Cell>();
        foreach (var r in rooms)
        {
            under.UnionWith(PolicyFunctions.RectCells(r.Region.Bounds()));
        }
        int soilScore = 0, rich = 0;
        foreach (var c in under)
        {
            var soil = G.Soil.GetValueOrDefault(c);
            soilScore -= PlanWeights.Soil * soil;
            if (soil == SoilCostRich)
            {
                rich++;
            }
            if (G.Rock.Contains(c))
            {
                soilScore -= PlanWeights.Rock;
            }
        }
        var footprint = 0;
        int w = S.Bounds.Width, h = S.Bounds.Height;
        foreach (var inside in PolicyFunctions.CoreFootprint(p, w, h))
        {
            if (inside)
            {
                footprint -= PlanWeights.Footprint;
            }
        }
        return output with
        {
            Soil = soilScore,
            RichCells = rich,
            Footprint = footprint,
            Edge = -EdgeCost(rooms, under),
            Centre = -CentreCost(rooms),
            Expansion = Expansion(rooms, under),
            Walk = -PlanWeights.Walk * PolicyFunctions.PlanWalk(p),
        };
    }

    /// <summary>Adds the terms read off PlanPerimeter's wall: wall length and defensibility (killbox, approach lane, gaps).</summary>
    public PlanScore Walled(LayoutPlan p, PlanScore output)
    {
        output = output with { Walled = true };
        if (p.Rooms.Count == 0)
        {
            return output;
        }
        var wp = PolicyFunctions.PlanPerimeter(p, S);
        var walls = new HashSet<Cell>();
        var gaps = 0;
        var (ring, hasRing) = wp.Ring();
        if (hasRing)
        {
            walls.UnionWith(ring.Footprint.Cells());
            gaps = ring.Ring!.Gaps.Area();
        }
        var killbox = wp.Killbox().Ok;
        var approach = wp.LiveSites(PolicyFunctions.NodeApproach).Count > 0;
        var defense = output.Defense;
        if (killbox)
        {
            defense += PlanWeights.Killbox;
        }
        if (approach)
        {
            defense += PlanWeights.Approach;
        }
        defense -= PlanWeights.Gap * gaps;
        return output with { Wall = -PlanWeights.Wall * walls.Count, Defense = defense };
    }

    /// <summary>
    /// Charges every cell of a room, its walls and door included, PlanWeights.Edge per step of walking distance it
    /// stands closer to an open map edge cell than EdgeClear, or a fifth of the map's short side on a small map. The
    /// ring cannot be built in the edge margin and raiders arrive at the edge. Rock the plan digs (under) walks like
    /// open ground, so a room in a mountain pays for every cell its dig or its walls expose to ground raiders can
    /// walk. A cell no raider can reach, natural rock left standing, costs nothing.
    /// </summary>
    internal int EdgeCost(IReadOnlyList<PlannedRoom> rooms, HashSet<Cell> under)
    {
        var clear = EdgeClear();
        var cost = 0;
        var dug = under.Where(c => G.Rock.Contains(c)).ToHashSet();
        var ground = Ground;
        if (dug.Count > 0)
        {
            ground = ground with { Walk = ground.EdgeWalk(dug) };
        }
        var seen = new HashSet<Cell>();
        foreach (var r in rooms)
        {
            foreach (var c in PolicyFunctions.RectCells(PolicyFunctions.Pad(r.Region.Bounds(), 1)))
            {
                if (!seen.Add(c))
                {
                    continue;
                }
                cost += EdgeCellCost(ground, c, clear);
            }
        }
        return cost;
    }

    /// <summary>Charges every room interior cell PlanWeights.Centre per step it stands closer to the map border than CentreClear, or a third of the map's short side on a small map. Plain geometry: unlike EdgeCost it ignores what raiders can walk.</summary>
    internal int CentreCost(IReadOnlyList<PlannedRoom> rooms)
    {
        var cost = 0;
        foreach (var r in rooms)
        {
            foreach (var c in PolicyFunctions.RectCells(r.Region.Bounds()))
            {
                cost += CentreCell(c);
            }
        }
        return cost;
    }

    /// <summary>The walking distance inside which a cell pays the edge term.</summary>
    internal int EdgeClear() => Math.Min(PlanWeights.EdgeClear, Math.Min(S.Bounds.Width, S.Bounds.Height) / 5);

    /// <summary>The edge term of one cell over ground: PlanWeights.Edge per step it stands inside clear of an open edge cell, 0 when no raider reaches it.</summary>
    internal static int EdgeCellCost(SiteGround ground, Cell c, int clear)
    {
        var d = ground.WalkDist(c);
        if (d < 0 || d >= clear)
        {
            return 0;
        }
        return PlanWeights.Edge * (clear - d);
    }

    /// <summary>The centre term of one room interior cell.</summary>
    internal int CentreCell(Cell c)
    {
        var b = S.Bounds;
        var clear = Math.Min(PlanWeights.CentreClear, Math.Min(b.Width, b.Height) / 3);
        var d = Math.Min(Math.Min(c.X, c.Z), Math.Min(b.Width - 1 - c.X, b.Height - 1 - c.Z));
        if (d >= clear)
        {
            return 0;
        }
        return PlanWeights.Centre * (clear - d);
    }

    /// <summary>The free ground a plan can still grow into: core cells within ExpansionReach of a room that are neither under the plan, rock nor rich soil, at most ExpansionCap of them.</summary>
    internal int Expansion(IReadOnlyList<PlannedRoom> rooms, HashSet<Cell> under)
    {
        var near = new HashSet<Cell>();
        foreach (var r in rooms)
        {
            foreach (var c in PolicyFunctions.RectCells(PolicyFunctions.Pad(r.Region.Bounds(), PlanWeights.ExpansionReach)))
            {
                if (G.Core.Contains(c) && !under.Contains(c) && !G.Rock.Contains(c) && G.Soil.GetValueOrDefault(c) != SoilCostRich)
                {
                    near.Add(c);
                }
            }
        }
        return PlanWeights.Expansion * Math.Min(near.Count, PlanWeights.ExpansionCap);
    }
}

/// <summary>Ports <c>layout_score.go</c>: plan scoring, one generator-independent score for a core plan, read by every siting search.</summary>
public static partial class PolicyFunctions
{
    internal static PlanScorer NewPlanScorer(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutReservation> reserved, MapSurvey s) =>
        new(BuildGround.Create(zones, reserved).WithSoil(s), s, NewSiteGround(s));

    /// <summary>Every colony's essential set: the rooms a plan that names none owes.</summary>
    internal static PlannedRole[] DefaultOwedRooms => [PlannedKitchen, PlannedFreezer, PlannedDining, PlannedWorkshop, PlannedStorage];

    /// <summary>The owed rooms rooms lack, one room per entry: a role owed twice needs two rooms. Owed empty is DefaultOwedRooms.</summary>
    internal static List<PlannedRole> MissingRooms(IEnumerable<PlannedRoom> rooms, IReadOnlyList<PlannedRole> owed)
    {
        IReadOnlyList<PlannedRole> want = owed.Count == 0 ? DefaultOwedRooms : owed;
        var have = new Dictionary<PlannedRole, int>();
        foreach (var r in rooms)
        {
            have[r.Role] = have.GetValueOrDefault(r.Role) + 1;
        }
        var missing = new List<PlannedRole>();
        foreach (var role in want)
        {
            if (have.GetValueOrDefault(role) == 0)
            {
                missing.Add(role);
                continue;
            }
            have[role]--;
        }
        return missing;
    }

    /// <summary>The cells a trip with no path counts as.</summary>
    internal const int WalkUnreachable = 200;

    /// <summary>
    /// The affinity-weighted walking distance over p: for every route trip edge, each room of the from role walks the
    /// shortest path over the walkable ground to the nearest to end, so every bedroom counts its own distance to
    /// dining. A trip with no path costs WalkUnreachable; one whose ends the plan lacks costs nothing. Not a score term:
    /// the corridor generator measures its gain with it.
    /// </summary>
    internal static int PlanWalk(LayoutPlan p)
    {
        if (p.Trips.Count > 0)
        {
            return TripWalk(p); // a tiled plan carries its own room trips
        }
        var (walk, rooms) = RouteWalk(p);
        var cells = TripCells(p, rooms);
        var total = 0;
        foreach (var trip in RouteTrips)
        {
            var to = cells(trip.To);
            var froms = cells(trip.From);
            if (to.Count == 0 || froms.Count == 0)
            {
                continue;
            }
            var goal = to.SelectMany(g => g).ToList();
            var dist = RouteDist(walk, goal);
            foreach (var from in froms)
            {
                var best = -1;
                foreach (var c in from)
                {
                    if (dist.TryGetValue(c, out var d) && (best < 0 || d < best))
                    {
                        best = d;
                    }
                }
                if (best < 0)
                {
                    best = WalkUnreachable;
                }
                total += trip.Weight * best;
            }
        }
        return total;
    }

    /// <summary>Every walk cell's 4-neighbour step distance to the nearest of goal.</summary>
    internal static Dictionary<Cell, int> RouteDist(IReadOnlyDictionary<Cell, int> walk, IEnumerable<Cell> goal)
    {
        var dist = new Dictionary<Cell, int>();
        var queue = new List<Cell>();
        foreach (var c in goal)
        {
            if (walk.ContainsKey(c) && dist.TryAdd(c, 0))
            {
                queue.Add(c);
            }
        }
        for (var head = 0; head < queue.Count; head++)
        {
            var c = queue[head];
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = new Cell(c.X + dx, c.Z + dz);
                if (!walk.ContainsKey(n) || dist.ContainsKey(n))
                {
                    continue;
                }
                dist[n] = dist[c] + 1;
                queue.Add(n);
            }
        }
        return dist;
    }
}
