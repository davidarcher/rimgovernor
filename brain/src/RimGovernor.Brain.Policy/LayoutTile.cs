namespace RimGovernor.Brain.Policy;

/// <summary>
/// Everything tiling reads. Embedding must come from Embed over Groups. OutdoorCurve and RoofDistance are the roofed
/// set's reads; an unknown read leaves the roofed set empty and the decision says so. FullRotRateC (the band a cold
/// room is held under) and SteelValue (the market value a build cost is counted in) are the airlocks' pricing reads;
/// either unknown leaves the airlocks unpriced. VentAvailable is whether the vent is researched. Light is the price of
/// darkness against lamps; invalid leaves the lamps unplaced. Sites are the power sites the site embedding placed
/// beside the rooms: stored on the plan, they join the conduit net. Graph, Furniture and Dining are what the interior
/// search reads (#2869); an empty Graph leaves every room with the greedy placer's layout. Comfort is the priced
/// comfort tier per node. NextRoomId is the plan's room id counter. Conduits are the links the colony holds: a link
/// that still holds is kept, so a replan moves only the conduits a change broke.
/// </summary>
public sealed record TileInput
{
    public IReadOnlyList<RoomGroup> Groups { get; init; } = [];
    public Embedding Embedding { get; init; } = default!;

    /// <summary>The blocks tiled: the embedding's, with the blocks of its held rooms (StationFacts.TileInputOf) in place of Embedding.Blocks.</summary>
    public IReadOnlyList<EmbeddedBlock> Blocks { get; init; } = [];
    public StationCatalog Catalog { get; init; } = default!;
    public IReadOnlyList<LayoutZone> Zones { get; init; } = [];
    public IReadOnlyList<LayoutReservation> Reservations { get; init; } = [];
    public MapSurvey Survey { get; init; } = default!;
    public Fact<IReadOnlyList<double>> OutdoorCurve { get; init; }
    public double RoofDistance { get; init; }
    public double FullRotRateC { get; init; }
    public double SteelValue { get; init; }
    public Fact<bool> VentAvailable { get; init; }
    public LightPrice Light { get; init; } = default!;
    public IReadOnlyList<PlannedSite> Sites { get; init; } = [];
    public StationGraph Graph { get; init; } = default!;
    public RoomFurniture Furniture { get; init; } = default!;
    public DiningFurniture Dining { get; init; } = default!;
    public IReadOnlyList<ComfortChoice> Comfort { get; init; } = [];
    public int NextRoomId { get; init; }
    public IReadOnlyList<ConduitLink> Conduits { get; init; } = [];
}

/// <summary>
/// One tiling decision for the caller to journal. Group is the room's group (the first of a link's two) and Other the
/// second group of a link decision, -1 otherwise. Field names the field of a field decision (Group is -1 then). DoorW
/// and AirlockW are an airlock decision's priced options: the plain door's and the airlock's total sustained watts
/// (operating plus build). DarkW and LampW are a lamp decision's priced options in sustained watts. Move is a kept
/// interior search move (SearchMove.Reason).
/// </summary>
public sealed record TileDecision
{
    public int Group { get; init; }
    public int Other { get; init; }
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Field { get; init; } = "";
    public double DoorW { get; init; }
    public double AirlockW { get; init; }
    public double DarkW { get; init; }
    public double LampW { get; init; }
    public string Move { get; init; } = "";
}

/// <summary>
/// The tiled plan with the furniture of every room and the decisions behind it. The tiler fills it as it goes, so it
/// is the one mutable carrier of the port: a result a failed tiling hands back is whatever had been laid when the
/// error stopped it.
/// </summary>
public sealed class TileResult
{
    public LayoutPlan Plan { get; set; } = new();

    /// <summary>Maps a group to its room's id.</summary>
    public Dictionary<int, int> Rooms { get; } = [];

    /// <summary>Each room's stations, set into its region.</summary>
    public Dictionary<int, IReadOnlyList<InteriorPiece>> Pieces { get; } = [];

    public List<TileDecision> Decisions { get; } = [];

    /// <summary>The conduit net's decisions, one per routed or blocked link.</summary>
    public List<ConduitDecision> ConduitDecisions { get; } = [];

    /// <summary>The roofed set's decisions.</summary>
    public IReadOnlyList<RoofDecision> RoofDecisions { get; set; } = [];

    /// <summary>The lamp cells of each lit room by room id.</summary>
    public Dictionary<int, IReadOnlyList<PlannedLamp>> Lamps { get; } = [];

    /// <summary>The groups whose room the placer could not furnish.</summary>
    public List<int> Unfit() => [.. Decisions.Where(d => d.Reason == PolicyFunctions.ReasonTileNoFit).Select(d => d.Group)];
}

/// <summary>ErrTile: an embedding that cannot be tiled into a valid plan. Partial is the result as far as the tiling got.</summary>
public class TileException(string message) : DomainException("tile embedding: " + message)
{
    public TileResult? Partial { get; internal set; }

    /// <summary>The station advice the tiling stood on, set by TileStations.</summary>
    public StationAdvice? Advice { get; internal set; }
}

/// <summary>ErrTileRole: a group whose room role no planned room carries.</summary>
public sealed class TileRoleException(string message) : TileException("group role has no planned room: " + message);

/// <summary>ErrTileClosed: rooms packed wall to wall leave a room with no door onto the ground and no neighbour it may be walked through, even marked Through.</summary>
public sealed class TileClosedException(string message) : TileException("packed rooms leave no way in: " + message);

/// <summary>A room of the tiler: its group, its block and its role.</summary>
internal readonly record struct TileRoom(int Group, Rectangle Rect, PlannedRole Role);

/// <summary>
/// Ports <c>layout_tile.go</c>. Tiling turns the embedding into a LayoutPlan: one room per placed group on the
/// group's block, a door graph over the walls the blocks share, an exterior door and entrance per room, and the
/// group's stations set into the room by PlaceInRegion. It is a pure function of the embedding and the survey. A
/// group's role comes from what the game makes of its stations and its cold need (a storeroom that stores cold is the
/// freezer); a group whose role no planned room carries stops the tiler with TileRoleException. The embedding's
/// rectangles are the rooms; walls are the ring around them, shared where two blocks stand a wall apart. A door joins
/// two blocks that share a wall when the groups have a required link, and for the heaviest flows otherwise as a
/// maximum spanning forest over the adjacent linked pairs, so no loop of doors opens shortcuts. Every room gets one
/// door onto open ground when its wall has a cell whose outside neighbour is free, buildable and joined to the map's
/// largest free area. A room that costs something to cross is never a thoroughfare: CheckRoutes finds a violation and
/// the tiler drops the crossed room's lightest link door that leaves every room joined to an exterior door, required
/// links last; when none can go, a room that MayBeThrough is marked Through. After the routes are kept, vents are
/// placed where the climate model prices them under holding the doors alone.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>Verdict of a tiling decision: the thing is placed.</summary>
    public const string TilePlace = "place";

    /// <summary>Verdict of a tiling decision: the thing cannot be satisfied.</summary>
    public const string TileUnsatisfied = "unsatisfied";

    public const string ReasonTileRoom = "tile_room";
    public const string ReasonTileRoomBuilt = "tile_room_built";
    public const string ReasonTileRoomMoved = "tile_room_successor";
    public const string ReasonTileLinkDoor = "tile_link_door";
    public const string ReasonTileExteriorDoor = "tile_exterior_door";
    public const string ReasonTileSealedRoom = "tile_sealed_room";
    public const string ReasonTileLinkDropped = "tile_link_dropped_thoroughfare";
    public const string ReasonTileWallStation = "tile_wall_station_not_set";
    public const string ReasonTileNoFit = "tile_no_fit";
    public const string ReasonTileRoutes = "tile_routes";
    public const string ReasonTileNoLink = "tile_required_link_not_adjacent";
    public const string ReasonTilePassThrough = "tile_pass_through";

    /// <summary>A kept interior search move (#2869); the decision's Move names the move and the gated terms it improved.</summary>
    public const string ReasonInteriorSearchMove = "interior_search_move";

    public const string ReasonTileAirlock = "tile_airlock";
    public const string ReasonTileAirlockNotPaid = "tile_airlock_not_paid";
    public const string ReasonTileAirlockNoGround = "tile_airlock_no_ground";
    public const string ReasonTileAirlockUnpriced = "tile_airlock_unpriced";
    public const string ReasonTileAirlockTemperate = "tile_airlock_temperate";

    /// <summary>Whether the group's room is kept cold: it needs cold or holds a cooler or cold storage.</summary>
    internal static bool GroupCold(RoomGroup g) =>
        g.Requires.Contains(EnvRequirement.Cold) || g.Stations.Any(s => s.Purpose == StationPurpose.Cool || s.Purpose == StationPurpose.StoreCold);

    /// <summary>The planned room a group becomes: the room role the game gives its stations, a storeroom that stores cold being the freezer. Ok is false when no planned room carries the group's role.</summary>
    public static (PlannedRole Role, bool Ok) PlannedRoleOfGroup(RoomGroup g)
    {
        if (g.Planned != PlannedRole.None)
        {
            return (g.Planned, true);
        }
        if (g.Role == RoomRole.Storeroom)
        {
            return GroupCold(g) ? (PlannedFreezer, true) : (PlannedStorage, true);
        }
        return PlannedRoleOf(g.Role);
    }

    /// <summary>The ordered pair of two room indices.</summary>
    internal static (int, int) PairOf(int i, int j) => j < i ? (j, i) : (i, j);

    /// <summary>
    /// Tiles the embedding into a plan. Groups the embedding could not place are left out (the embedding reports
    /// them). A room that cannot be reached throws a TileException carrying the partial result. A room the placer
    /// cannot furnish stands bare with a TileUnsatisfied decision (TileResult.Unfit).
    /// </summary>
    public static TileResult TileEmbedding(TileInput input)
    {
        var t = new Tiler(input);
        try
        {
            t.Run();
        }
        catch (TileException e)
        {
            e.Partial ??= t.Out;
            throw;
        }
        return t.Out;
    }

    /// <summary>Whether crossing r costs less the lower it ranks, higher first: by what crossing the neighbour triggers (RoomCrossing.Mechanisms), a circulation room first, and a cold room, which is never crossed, last.</summary>
    internal static long ThroughRank(PlannedRoom r)
    {
        if (r.Role == PlannedRole.Circulation)
        {
            return long.MaxValue;
        }
        if (r.Crossing is null)
        {
            return 0;
        }
        if (!r.Crossing.Crossable())
        {
            return long.MinValue;
        }
        return -r.Crossing.Mechanisms();
    }

    /// <summary>The buildable cells outside every room and wall that join the largest such area: an entrance in a pocket the rooms close in is no entrance.</summary>
    internal static HashSet<Cell> TileOpenGround(PlanScorer scorer, HashSet<Cell> occupied)
    {
        var cells = new List<Cell>();
        foreach (var c in scorer.G.Core)
        {
            if (!occupied.Contains(c))
            {
                cells.Add(c);
            }
        }
        SortCells(cells);
        var free = new HashSet<Cell>(cells);
        List<Cell> best = [];
        var seen = new HashSet<Cell>();
        foreach (var c in cells)
        {
            if (!seen.Add(c))
            {
                continue;
            }
            var comp = new List<Cell> { c };
            for (var h = 0; h < comp.Count; h++)
            {
                var p = comp[h];
                foreach (var n in new[] { new Cell(p.X + 1, p.Z), new Cell(p.X - 1, p.Z), new Cell(p.X, p.Z + 1), new Cell(p.X, p.Z - 1) })
                {
                    if (free.Contains(n) && seen.Add(n))
                    {
                        comp.Add(n);
                    }
                }
            }
            if (comp.Count > best.Count)
            {
                best = comp;
            }
        }
        return [.. best];
    }

    /// <summary>The room's layout: the greedy placer's, improved by the interior search when req asks for it, with the kept moves.</summary>
    internal static (IReadOnlyList<InteriorPiece> Pieces, IReadOnlyList<SearchMove> Moves) PlaceAndSearch(RegionPlacement req)
    {
        if (req.Search is null)
        {
            return (PlaceInRegion(req), []);
        }
        var res = SearchInterior(req, req.Search);
        return (res.Stood(), res.Moves);
    }
}

/// <summary>The tiler's state: the rooms, the door graph chosen so far, the vents and the plan under construction.</summary>
internal sealed partial class Tiler(TileInput input)
{
    internal TileInput In { get; } = input;
    internal List<TileRoom> Rooms { get; } = [];
    internal Dictionary<(int, int), double> Weight { get; } = [];
    internal Dictionary<(int, int), bool> Required { get; } = [];

    /// <summary>Union-find over the rooms the link doors join.</summary>
    internal List<int> Parent { get; set; } = [];

    internal Dictionary<(int, int), List<Cell>> Adj { get; } = [];
    internal Dictionary<(int, int), Cell> Links { get; } = [];
    internal Dictionary<int, bool> Through { get; } = [];
    internal List<Cell?> ExtDoor { get; set; } = [];
    internal List<Cell> ExtOut { get; set; } = [];

    /// <summary>Open ground, as ChooseExteriors found it.</summary>
    internal HashSet<Cell> Free { get; set; } = [];

    internal List<TileVent> Vents { get; } = [];
    internal LayoutPlan Plan { get; set; } = new();
    internal TileResult Out { get; } = new();

    internal void Run()
    {
        var scorer = new PlanScorer(In.Zones, In.Reservations, In.Survey);
        var index = new Dictionary<int, int>();
        var blocks = In.Blocks.ToList();
        var placed = PlacedBlocks(In.Groups, blocks);
        blocks = [.. blocks.OrderBy(b => b.Group)];
        Plan = new LayoutPlan { Zones = In.Zones, Reservations = In.Reservations, Sites = [.. In.Sites], NextRoomId = In.NextRoomId };
        this.FieldZones(); // the placed fields, no walls or doors (layout_field_place.go)

        // Every group owes a room, placed or not: a group the embedding left out stays in the hard tier's Missing term
        // instead of hiding behind another group of its role. A role no planned room carries is stopped below when its
        // group is placed, else left to the embedding's report.
        var owed = new List<PlannedRole>(Plan.Owed);
        foreach (var g in In.Groups)
        {
            var (role, ok) = PlannedRoleOfGroup(g);
            if (ok)
            {
                owed.Add(role);
            }
        }
        Plan = Plan with { Owed = owed };

        var rooms = new List<PlannedRoom>(Plan.Rooms);
        foreach (var b in blocks)
        {
            if (b.Group < 0 || b.Group >= In.Groups.Count)
            {
                throw new TileException($"block of unknown group {b.Group}");
            }
            var g = In.Groups[b.Group];
            var (role, ok) = PlannedRoleOfGroup(g);
            if (!ok)
            {
                throw new TileRoleException($"group {g.Id} predicts {g.Role}");
            }
            foreach (var s in g.Stations)
            {
                if (!In.Catalog.Station(s.Def).Ok)
                {
                    throw new TileException($"station {s.Def} of group {g.Id} is not in the catalog");
                }
            }
            index[b.Group] = Rooms.Count;
            Rooms.Add(new TileRoom(b.Group, b.Interior, role));
            var room = new PlannedRoom
            {
                Id = b.Built,
                Replaces = b.Replaces,
                Role = role,
                Region = RectRegion(b.Interior),
                Crossing = CrossingOf(g),
                Outdoor = role.IsOutdoor(),
                Markers = g.Markers,
                WorkChairs = WorkChairsOf(g, In.Comfort),
            };
            if (role == PlannedTrainingRange && AwayReference(In.Groups, placed) is { } reference)
            {
                room = room with { Facing = DummyFacing(b.Interior, reference) };
            }
            room = room with { Dug = scorer.G.Dug(room) };
            rooms.Add(room);
        }
        Plan = Plan with { Rooms = rooms };
        Plan = Plan.WithRoomIDs();
        for (var i = 0; i < Rooms.Count; i++)
        {
            var r = Rooms[i];
            Out.Rooms[r.Group] = Plan.Rooms[i].Id;
            var reason = ReasonTileRoom;
            var b = blocks[blocks.FindIndex(x => x.Group == r.Group)];
            if (b.Built != 0)
            {
                reason = ReasonTileRoomBuilt; // the standing room, kept as it stands
            }
            else if (b.Replaces != 0)
            {
                reason = ReasonTileRoomMoved; // a successor for the standing room it replaces
            }
            Note(r.Group, -1, TilePlace, reason);
        }
        foreach (var b in blocks)
        {
            foreach (var l in In.Groups[b.Group].Links)
            {
                if (!index.TryGetValue(l.To, out var j) || l.To == b.Group)
                {
                    continue;
                }
                var k = PairOf(index[b.Group], j);
                if (b.Group < l.To)
                {
                    Weight[k] = Weight.GetValueOrDefault(k) + l.Weight;
                }
                Required[k] = Required.GetValueOrDefault(k) || l.Required;
            }
        }
        for (var i = 0; i < Rooms.Count; i++)
        {
            for (var j = i + 1; j < Rooms.Count; j++)
            {
                if (Open(i) || Open(j))
                {
                    continue; // an unfenced room has no wall to share
                }
                var run = SharedWallRun(Pad(Rooms[i].Rect, 1), Pad(Rooms[j].Rect, 1));
                if (run.Count > 0)
                {
                    Adj[(i, j)] = [.. run];
                }
            }
        }
        foreach (var (k, required) in Required.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
        {
            if (required && Adj.GetValueOrDefault(k) is not { Count: > 0 } && !Open(k.Item1) && !Open(k.Item2))
            {
                Note(Rooms[k.Item1].Group, Rooms[k.Item2].Group, TileUnsatisfied, ReasonTileNoLink);
            }
        }
        Parent = [.. Enumerable.Range(0, Rooms.Count)];
        ChooseLinks(true);
        ChooseExteriors(scorer);
        JoinSealed();
        ChooseLinks(false);
        KeepRoutes();
        ChooseVents();
        Out.Plan = Build();
        if (!Out.Plan.Valid())
        {
            throw new TileException("the tiled plan is not valid");
        }
        Furnish();
        var roof = PlanRoofed(Out.Plan, RoofInputOf());
        Out.Plan = Out.Plan with { Roofed = roof.Roofed };
        Out.RoofDecisions = roof.Decisions;
        if (PlaceAirlocks())
        {
            // the vestibules are rooms too: roof them
            roof = PlanRoofed(Out.Plan, RoofInputOf());
            Out.Plan = Out.Plan with { Roofed = roof.Roofed };
            Out.RoofDecisions = roof.Decisions;
        }
        this.PlaceLamps();
        Out.Plan = Out.Plan with { Lamps = Out.Lamps.ToDictionary(e => e.Key, e => e.Value) };
        this.RouteTileConduits();
        if (!Out.Plan.Valid())
        {
            throw new TileException("the conduit net is not valid");
        }
    }

    /// <summary>Whether room i is unfenced: it has no wall, door or entrance, and every pawn reaches it over open ground.</summary>
    internal bool Open(int i) => Plan.Rooms[i].Unfenced();

    internal void Note(int group, int other, string verdict, string reason) =>
        Out.Decisions.Add(new TileDecision { Group = group, Other = other, Verdict = verdict, Reason = reason });

    internal int Find(int i)
    {
        if (Parent[i] != i)
        {
            Parent[i] = Find(Parent[i]);
        }
        return Parent[i];
    }

    /// <summary>The doors room i has so far: its exterior door and its links.</summary>
    internal List<Cell> DoorsOf(int i)
    {
        var doors = LinkCells(i);
        if (i < ExtDoor.Count && ExtDoor[i] is { } d)
        {
            doors.Add(d);
        }
        return doors;
    }

    /// <summary>
    /// The door cell of the shared wall of pair k, and whether the placer furnishes both rooms behind it: the middle
    /// of the run when it does, else the first cell of the run that does, else the middle.
    /// </summary>
    internal (Cell Cell, bool Fits) PickLink((int, int) k)
    {
        var run = Adj[k];
        var middle = run[(run.Count - 1) / 2];
        bool Fits(Cell c) => Furnishes(k.Item1, [.. DoorsOf(k.Item1), c]) && Furnishes(k.Item2, [.. DoorsOf(k.Item2), c]);
        if (Fits(middle))
        {
            return (middle, true);
        }
        foreach (var c in run)
        {
            if (Fits(c))
            {
                return (c, true);
            }
        }
        return (middle, false);
    }

    /// <summary>
    /// Opens a door on every adjacent required pair (required) or on the heaviest adjacent flows that join two rooms
    /// not yet joined. A flow door is a wish, opened only while the placer still furnishes both rooms behind it.
    /// </summary>
    internal void ChooseLinks(bool required)
    {
        var pairs = Adj.Keys.ToList();
        pairs.Sort((p, q) =>
        {
            var rp = Required.GetValueOrDefault(p);
            var rq = Required.GetValueOrDefault(q);
            if (rp != rq)
            {
                return rp ? -1 : 1;
            }
            var wp = Weight.GetValueOrDefault(p);
            var wq = Weight.GetValueOrDefault(q);
            if (wp != wq)
            {
                return wp > wq ? -1 : 1;
            }
            return p.Item1 != q.Item1 ? p.Item1.CompareTo(q.Item1) : p.Item2.CompareTo(q.Item2);
        });
        foreach (var k in pairs)
        {
            if (Required.GetValueOrDefault(k) != required)
            {
                continue;
            }
            var (cell, fits) = PickLink(k);
            if (!required && (Weight.GetValueOrDefault(k) <= 0 || Find(k.Item1) == Find(k.Item2) || !fits || SleepersUseCirculation(k)))
            {
                continue;
            }
            Links[k] = cell;
            Parent[Find(k.Item1)] = Find(k.Item2);
            Note(Rooms[k.Item1].Group, Rooms[k.Item2].Group, TilePlace, ReasonTileLinkDoor);
        }
    }

    /// <summary>A flow door between a room with beds and a room that is not circulation, while the plan has circulation: the sleepers' way to the hubs is the corridor, so no hub carries a bedroom door.</summary>
    internal bool SleepersUseCirculation((int, int) k)
    {
        if (!Plan.Rooms.Any(r => r.Role == PlannedRole.Circulation))
        {
            return false;
        }
        bool Sleeps(int i) => Plan.Rooms[i].Crossing is { Beds: > 0 };
        bool Circulation(int i) => Plan.Rooms[i].Role == PlannedRole.Circulation;
        return (Sleeps(k.Item1) || Sleeps(k.Item2)) && !Circulation(k.Item1) && !Circulation(k.Item2);
    }

    /// <summary>Gives every room the door nearest the middle of the blocks whose outside cell is open ground.</summary>
    internal void ChooseExteriors(PlanScorer scorer)
    {
        var occupied = new HashSet<Cell>();
        long cx = 0;
        long cz = 0;
        foreach (var r in Rooms)
        {
            foreach (var c in RectCells(Pad(r.Rect, 1)))
            {
                occupied.Add(c);
            }
            cx += r.Rect.X + (r.Rect.Width / 2);
            cz += r.Rect.Z + (r.Rect.Height / 2);
        }
        var open = TileOpenGround(scorer, occupied);
        Free = open;
        var centre = default(Cell);
        long n = Rooms.Count;
        if (n > 0)
        {
            centre = new Cell((int)(cx / n), (int)(cz / n));
        }
        ExtDoor = [.. Enumerable.Repeat((Cell?)null, Rooms.Count)];
        ExtOut = [.. Enumerable.Repeat(default(Cell), Rooms.Count)];
        for (var i = 0; i < Rooms.Count; i++)
        {
            var r = Rooms[i];
            var cands = new List<(Cell Door, Cell Out)>();
            var rc = r.Rect;
            for (var x = rc.X; x < rc.X + rc.Width; x++)
            {
                cands.Add((new Cell(x, rc.Z - 1), new Cell(x, rc.Z - 2)));
                cands.Add((new Cell(x, rc.Z + rc.Height), new Cell(x, rc.Z + rc.Height + 1)));
            }
            for (var z = rc.Z; z < rc.Z + rc.Height; z++)
            {
                cands.Add((new Cell(rc.X - 1, z), new Cell(rc.X - 2, z)));
                cands.Add((new Cell(rc.X + rc.Width, z), new Cell(rc.X + rc.Width + 1, z)));
            }
            cands.RemoveAll(c => !open.Contains(c.Out));
            cands.Sort((a, b) =>
            {
                var da = SquaredDistance(a.Door, centre);
                var db = SquaredDistance(b.Door, centre);
                if (da != db)
                {
                    return da < db ? -1 : 1;
                }
                return CellLess(a.Door, b.Door) ? -1 : CellLess(b.Door, a.Door) ? 1 : 0;
            });
            if (cands.Count == 0 || Open(i))
            {
                continue;
            }

            // The placer is the judge of a door: each door keeps its threshold clear, so the first door, nearest the
            // middle, that leaves the group's stations a place is taken, else the nearest.
            var pick = cands[0];
            var links = LinkCells(i);
            foreach (var c in cands)
            {
                if (Furnishes(i, [c.Door, .. links]))
                {
                    pick = c;
                    break;
                }
            }
            ExtDoor[i] = pick.Door;
            ExtOut[i] = pick.Out;
            Note(r.Group, -1, TilePlace, ReasonTileExteriorDoor);
        }
    }

    /// <summary>The link doors of room i.</summary>
    internal List<Cell> LinkCells(int i)
    {
        var output = new List<Cell>();
        foreach (var (k, cell) in Links)
        {
            if (k.Item1 == i || k.Item2 == i)
            {
                output.Add(cell);
            }
        }
        SortCells(output);
        return output;
    }

    /// <summary>The floor pieces of room i's group; a wall building stands in the wall ring and is left out (walls is set).</summary>
    internal (List<RegionWant> Wants, bool Walls) Wants(int i)
    {
        var wants = new List<RegionWant>();
        var walls = false;
        foreach (var s in In.Groups[Rooms[i].Group].Stations)
        {
            var (st, _) = In.Catalog.Station(s.Def); // TileEmbedding checked every def
            if (st.Access == StationAccess.Wall)
            {
                walls = true;
                continue;
            }
            wants.Add(new RegionWant { Slot = s.Node, Def = s.Def, Size = st.Shape.Size, Interaction = st.Shape.Interaction, Count = s.Count, Min = s.Count });
        }
        return (wants, walls);
    }

    /// <summary>Whether the placer sets room i's stations behind doors.</summary>
    internal bool Furnishes(int i, IReadOnlyList<Cell> doors)
    {
        var (wants, _) = Wants(i);
        try
        {
            PlaceInRegion(new RegionPlacement { Region = RectRegion(Rooms[i].Rect), Doors = EquatableArray.From(doors), Wants = EquatableArray.From(wants) });
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>Which rooms are joined by link doors to a room with an exterior door.</summary>
    internal bool[] Reach()
    {
        var ok = new bool[Rooms.Count];
        var queue = new List<int>();
        for (var i = 0; i < Rooms.Count; i++)
        {
            if (ExtDoor[i] is not null || Open(i))
            {
                ok[i] = true;
                queue.Add(i);
            }
        }
        for (var h = 0; h < queue.Count; h++)
        {
            foreach (var k in Links.Keys)
            {
                foreach (var (from, to) in new[] { (k.Item1, k.Item2), (k.Item2, k.Item1) })
                {
                    if (from == queue[h] && !ok[to])
                    {
                        ok[to] = true;
                        queue.Add(to);
                    }
                }
            }
        }
        return ok;
    }

    /// <summary>
    /// Opens a link door from every room no exterior door reaches into an adjacent room that is reached, preferring a
    /// neighbour a room may be walked through (ThroughRank), then the heavier flow.
    /// </summary>
    internal void JoinSealed()
    {
        while (true)
        {
            var ok = Reach();
            (int, int) best = default;
            double bestScore = 0;
            long bestThrough = -1;
            var bestFits = false;
            var found = false;
            var stranded = false;
            foreach (var k in Adj.Keys)
            {
                foreach (var (a, b) in new[] { (k.Item1, k.Item2), (k.Item2, k.Item1) })
                {
                    if (ok[a])
                    {
                        continue;
                    }
                    stranded = true;
                    if (!ok[b])
                    {
                        continue;
                    }
                    var score = Weight.GetValueOrDefault(k);
                    var through = ThroughRank(Plan.Rooms[b]);
                    if (Links.ContainsKey(k))
                    {
                        continue;
                    }
                    var fits = PickLink(k).Fits;
                    var same = through == bestThrough && fits == bestFits;
                    var better = through > bestThrough || (through == bestThrough && fits != bestFits && fits) || (same && score > bestScore);
                    if (!found || better || (same && score == bestScore && ((k.Item1 * Rooms.Count) + k.Item2) < ((best.Item1 * Rooms.Count) + best.Item2)))
                    {
                        best = k;
                        bestScore = score;
                        bestThrough = through;
                        bestFits = fits;
                        found = true;
                    }
                }
            }
            if (!found)
            {
                if (!stranded)
                {
                    return;
                }
                for (var i = 0; i < Rooms.Count; i++)
                {
                    if (!ok[i])
                    {
                        Note(Rooms[i].Group, -1, TileUnsatisfied, ReasonTileSealedRoom);
                        throw new TileClosedException($"group {Rooms[i].Group}");
                    }
                }
                return;
            }
            Links[best] = PickLink(best).Cell;
            Parent[Find(best.Item1)] = Find(best.Item2);
            Note(Rooms[best.Item1].Group, Rooms[best.Item2].Group, TilePlace, ReasonTileSealedRoom);
        }
    }

    /// <summary>Tiles until CheckRoutes accepts the plan: a pass-through room loses its lightest link door that strands no room.</summary>
    internal void KeepRoutes()
    {
        while (true)
        {
            Out.Plan = Build();
            var check = CheckRoutes(Out.Plan);
            if (check.Thoroughfare is not { } through)
            {
                if (check.Error is not null)
                {
                    throw new TileException(check.Error);
                }
                return;
            }
            var room = -1;
            for (var i = 0; i < Out.Plan.Rooms.Count; i++)
            {
                if (Out.Plan.Rooms[i].Id == through.Room)
                {
                    room = i;
                }
            }
            var drop = Links.Keys.Where(k => k.Item1 == room || k.Item2 == room).ToList();
            drop.Sort((p, q) =>
            {
                var rp = Required.GetValueOrDefault(p);
                var rq = Required.GetValueOrDefault(q);
                if (rp != rq)
                {
                    return rp ? 1 : -1;
                }
                var wp = Weight.GetValueOrDefault(p);
                var wq = Weight.GetValueOrDefault(q);
                if (wp != wq)
                {
                    return wp < wq ? -1 : 1;
                }
                return p.Item1 != q.Item1 ? p.Item1.CompareTo(q.Item1) : p.Item2.CompareTo(q.Item2);
            });
            var dropped = false;
            foreach (var k in drop)
            {
                var cell = Links[k];
                Links.Remove(k);
                if (Reach().Contains(false))
                {
                    Links[k] = cell;
                    continue;
                }
                Note(Rooms[k.Item1].Group, Rooms[k.Item2].Group, TileUnsatisfied, ReasonTileLinkDropped);
                dropped = true;
                break;
            }
            if (!dropped && room >= 0 && Plan.Rooms[room].MayBeThrough() && !Through.GetValueOrDefault(room))
            {
                // No door can go without stranding a room: the packed base has no other way in, so the private room is
                // walked through.
                Through[room] = true;
                Note(Rooms[room].Group, -1, TilePlace, ReasonTilePassThrough);
                continue;
            }
            if (!dropped)
            {
                Note(Rooms[Math.Max(room, 0)].Group, -1, TileUnsatisfied, ReasonTileRoutes);
                throw new TileClosedException(through.Message);
            }
        }
    }

    /// <summary>The plan over the doors chosen so far.</summary>
    internal LayoutPlan Build()
    {
        var rooms = new List<PlannedRoom>(Plan.Rooms);
        var links = new List<RoomLink>();
        foreach (var (k, cell) in Links)
        {
            links.Add(new RoomLink(Math.Min(rooms[k.Item1].Id, rooms[k.Item2].Id), Math.Max(rooms[k.Item1].Id, rooms[k.Item2].Id), cell));
        }
        links.Sort((a, b) =>
        {
            if (a.A != b.A)
            {
                return a.A.CompareTo(b.A);
            }
            if (a.B != b.B)
            {
                return a.B.CompareTo(b.B);
            }
            return CellLess(a.Cell, b.Cell) ? -1 : CellLess(b.Cell, a.Cell) ? 1 : 0;
        });
        var entrances = new List<Cell>();
        for (var i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i] with { Through = Through.GetValueOrDefault(i) };
            Cell door;
            if (ExtDoor[i] is { } ext)
            {
                door = ext;
                entrances.Add(ExtOut[i]);
            }
            else
            {
                door = default;
                foreach (var l in links)
                {
                    if (l.A == r.Id || l.B == r.Id)
                    {
                        door = l.Cell;
                        break;
                    }
                }
            }
            if (r.Unfenced())
            {
                rooms[i] = r; // no ring, no door
                continue;
            }
            var (rot, _) = DoorSide(r.Region.Bounds(), door);
            rooms[i] = r with { Door = door, DoorRot = rot };
        }
        SortCells(entrances);
        var compact = new List<Cell>();
        foreach (var c in entrances)
        {
            if (compact.Count == 0 || compact[^1] != c)
            {
                compact.Add(c);
            }
        }
        var trips = new List<RoomTrip>();
        foreach (var (k, w) in Weight)
        {
            if (w > 0)
            {
                trips.Add(new RoomTrip(Math.Min(rooms[k.Item1].Id, rooms[k.Item2].Id), Math.Max(rooms[k.Item1].Id, rooms[k.Item2].Id), w));
            }
        }
        trips.Sort((a, b) => a.A != b.A ? a.A.CompareTo(b.A) : a.B.CompareTo(b.B));
        var outside = new List<OutsideTrip>();
        for (var i = 0; i < Rooms.Count; i++)
        {
            var w = In.Groups[Rooms[i].Group].Outside;
            if (w > 0)
            {
                outside.Add(new OutsideTrip(rooms[i].Id, w));
            }
        }
        return Plan with
        {
            Rooms = rooms,
            Links = links,
            Entrances = compact,
            Sites = [.. Plan.Sites, .. VentSites()],
            Trips = trips,
            OutsideTrips = outside,
        };
    }

    /// <summary>Sets every room's stations into its region, behind its doors.</summary>
    internal void Furnish()
    {
        for (var i = 0; i < Out.Plan.Rooms.Count; i++)
        {
            var room = Out.Plan.Rooms[i];
            var g = In.Groups[Rooms[i].Group];
            var doors = new List<Cell> { room.Door };
            doors.AddRange(Out.Plan.LinkCells(room.Id));
            SortCells(doors);
            doors = [.. doors.Distinct()];
            var (wants, walls) = Wants(i);
            if (walls)
            {
                // A wall building stands in the wall ring, not on the floor.
                Note(g.Id, -1, TilePlace, ReasonTileWallStation);
            }
            var req = new RegionPlacement { Region = room.Region, Doors = EquatableArray.From(doors), Wants = EquatableArray.From(wants) };
            if (In.Graph.Nodes.Count > 0)
            {
                req = req with { Search = InteriorSearchOf(room.Id, g.Role, doors) };
            }
            IReadOnlyList<InteriorPiece> pieces;
            IReadOnlyList<SearchMove> moves;
            try
            {
                (pieces, moves) = PlaceAndSearch(req);
            }
            catch (DomainException)
            {
                // The group's area undercounts what the placer needs: the room stands without furniture and the
                // decision says so.
                Note(g.Id, -1, TileUnsatisfied, ReasonTileNoFit);
                continue;
            }
            Out.Pieces[room.Id] = pieces;
            foreach (var m in moves)
            {
                Out.Decisions.Add(new TileDecision { Group = g.Id, Other = -1, Verdict = TilePlace, Reason = ReasonInteriorSearchMove, Move = m.Slot + ":" + m.Reason() });
            }
        }
    }

    /// <summary>
    /// Room id's search (#2869): seeded from the room id and the siting seed of the embedding the plan stands on, so
    /// equal inputs tile alike and no wall time enters; the doors the tiler chose are the only candidates; the flows
    /// are the station graph's, the beds taking the room's purpose; the shapes are the catalog's rows. The tiler has
    /// no built input yet, so nothing is pinned or moved free.
    /// </summary>
    internal InteriorSearch InteriorSearchOf(int id, RoomRole role, IReadOnlyList<Cell> doors)
    {
        var defs = In.Catalog.Stations.ToDictionary(e => e.Key, e => e.Value.Shape);
        var siting = new SearchRand(In.Embedding.Seed).State;
        return new InteriorSearch
        {
            Candidates = doors,
            Shapes = new PieceShapes { Defs = defs, Furniture = In.Furniture },
            Dining = In.Dining,
            Flows = new GraphFlows(In.Graph, SleepPurposeOf(role), In.Furniture),
            Seed = unchecked(siting ^ ((ulong)(uint)id * 0x9e3779b97f4a7c15UL)),
        };
    }

    /// <summary>What the roofed set reads from the tiler's input.</summary>
    internal RoofInput RoofInputOf() => new() { Curve = In.OutdoorCurve, Cold = In.Survey.Cold, Hot = In.Survey.Hot, RoofDistance = In.RoofDistance };
}
