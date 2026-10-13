namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the game charges for walking through a room, read from the station group that became it. No role is looked up:
/// the three mechanisms are facts about the stations inside. Cold: the room is kept cold, so every door opening
/// equalizes its temperature with the next room and walking through leaks the cold (never crossed). Beds:
/// player-faction beds, where clamor within 7 cells gives a sleeper the SleepDisturbed memory. Clean: a station's
/// effect scales with the room's cleanliness and pawns track filth in.
/// </summary>
public sealed record RoomCrossing
{
    public bool Cold { get; init; }
    public int Beds { get; init; }
    public bool Clean { get; init; }

    /// <summary>How many of the crossing costs walking through the room triggers; the cold mechanism is not priced but forbids (Crossable).</summary>
    public int Mechanisms() => (Beds > 0 ? 1 : 0) + (Clean ? 1 : 0);

    /// <summary>Whether crossing is a cost to price, not a ban: only a cold room is never crossed.</summary>
    public bool Crossable() => !Cold;
}

/// <summary>A trip whose shortest path crosses a room nobody may walk through; Room is that room's id.</summary>
public sealed record ThroughfareError
{
    public PlannedRole From { get; init; }
    public PlannedRole To { get; init; }
    public PlannedRole Crossed { get; init; }
    public int Room { get; init; }
    public Cell Cell { get; init; }

    public string Message => $"{From} -> {To} crosses {Crossed} at {{{Cell.X} {Cell.Z}}}";
}

/// <summary>The result of CheckRoutes: how many paths cross each cell, or the first error (a ThroughfareError among them).</summary>
public sealed record RouteCheck
{
    public IReadOnlyDictionary<Cell, int> Traffic { get; init; } = new Dictionary<Cell, int>();

    /// <summary>The error text; null when the routes are valid.</summary>
    public string? Error { get; init; }

    /// <summary>Set when the error is a thoroughfare.</summary>
    public ThroughfareError? Thoroughfare { get; init; }

    public bool Ok => Error is null;
}

public sealed partial record PlannedRoom
{
    /// <summary>Whether the plan may mark the room Through: it has crossing facts, is not cold and crossing it costs something.</summary>
    public bool MayBeThrough() => Crossing is not null && Crossing.Crossable() && Crossing.Mechanisms() > 0;
}

/// <summary>The walkable cells over their bounding box, so a path search indexes arrays instead of hashing cells. Search state is stamped per search, so one grid serves every trip without clearing.</summary>
public sealed class RouteGrid
{
    private readonly int minX;
    private readonly int minZ;
    private readonly int w;
    private readonly int h;
    private readonly int[] room = [];
    private readonly uint[] seen = [];
    private readonly uint[] goal = [];
    private readonly int[] prev = [];
    private uint stamp;

    public RouteGrid(IReadOnlyDictionary<Cell, int> walk)
    {
        var first = true;
        int maxX = 0, maxZ = 0;
        foreach (var c in walk.Keys)
        {
            if (first)
            {
                (minX, maxX, minZ, maxZ, first) = (c.X, c.X, c.Z, c.Z, false);
                continue;
            }
            (minX, maxX) = (Math.Min(minX, c.X), Math.Max(maxX, c.X));
            (minZ, maxZ) = (Math.Min(minZ, c.Z), Math.Max(maxZ, c.Z));
        }
        if (first)
        {
            return;
        }
        (w, h) = (maxX - minX + 1, maxZ - minZ + 1);
        var n = w * h;
        (room, seen, goal, prev) = (new int[n], new uint[n], new uint[n], new int[n]);
        foreach (var (c, i) in walk)
        {
            room[Index(c)] = i + 2;
        }
    }

    public bool In(Cell c) => c.X >= minX && c.Z >= minZ && c.X < minX + w && c.Z < minZ + h;

    private int Index(Cell c) => ((c.Z - minZ) * w) + (c.X - minX);

    private Cell CellAt(int i) => new(minX + (i % w), minZ + (i / w));

    /// <summary>The room index of a walkable cell (-1 hallway or door).</summary>
    public int At(Cell c) => room[Index(c)] - 2;

    private static readonly (int, int)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>A shortest 4-neighbour path over the grid from any of from to any of to, or null.</summary>
    public List<Cell>? RoutePath(IEnumerable<Cell> from, IEnumerable<Cell> to)
    {
        stamp++;
        foreach (var c in to)
        {
            if (In(c))
            {
                goal[Index(c)] = stamp;
            }
        }
        var queue = new List<int>();
        foreach (var c in from)
        {
            if (In(c) && room[Index(c)] != 0)
            {
                var i = Index(c);
                (seen[i], prev[i]) = (stamp, i);
                queue.Add(i);
            }
        }
        for (var head = 0; head < queue.Count; head++)
        {
            var i = queue[head];
            if (goal[i] == stamp)
            {
                var path = new List<Cell> { CellAt(i) };
                while (prev[i] != i)
                {
                    i = prev[i];
                    path.Add(CellAt(i));
                }
                return path;
            }
            var c = CellAt(i);
            foreach (var (dx, dz) in Steps)
            {
                var n = new Cell(c.X + dx, c.Z + dz);
                if (!In(n))
                {
                    continue;
                }
                var ni = Index(n);
                if (room[ni] == 0 || seen[ni] == stamp)
                {
                    continue;
                }
                (seen[ni], prev[ni]) = (stamp, i);
                queue.Add(ni);
            }
        }
        return null;
    }

    /// <summary>
    /// Demands that pawns can walk from an entrance into every room of a tiled plan (the rooms with crossing facts)
    /// without crossing a room nobody may walk through: the trips cover only some roles, a room no trip ends in could
    /// be sealed or behind a closed room. A room reached only across a closed room is a ThroughfareError naming the
    /// first such room on the shortest way in. Returns the error text, null when every room is reached.
    /// </summary>
    public RouteCheck? CheckReachable(IReadOnlyList<Cell> entrances, IReadOnlyList<PlannedRoom> rooms)
    {
        var tiled = rooms.Count(r => r.Crossing is not null);
        if (tiled == 0)
        {
            return null;
        }
        if (entrances.Count == 0)
        {
            return new RouteCheck { Error = $"no entrances to reach {tiled} rooms" };
        }
        var closed = new bool[rooms.Count];
        for (var i = 0; i < rooms.Count; i++)
        {
            closed[i] = !PolicyFunctions.WalkedThrough(rooms[i]);
        }
        var reached = Flood(entrances, closed, rooms.Count);
        for (var i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            if (reached[i] || r.Crossing is null)
            {
                continue;
            }
            var to = new List<Cell>();
            var ins = r.Region.Bounds();
            for (var x = ins.X; x < ins.X + ins.Width; x++)
            {
                for (var z = ins.Z; z < ins.Z + ins.Height; z++)
                {
                    var c = new Cell(x, z);
                    if (In(c) && At(c) == i)
                    {
                        to.Add(c);
                    }
                }
            }
            var path = RoutePath(entrances, to);
            var unreachable = new RouteCheck { Error = $"room {r.Id} ({r.Role}) is not reachable from the entrances" };
            if (path is null)
            {
                return unreachable;
            }
            foreach (var c in path)
            {
                var j = At(c);
                if (j >= 0 && j != i && closed[j])
                {
                    var err = new ThroughfareError { To = r.Role, Crossed = rooms[j].Role, Room = rooms[j].Id, Cell = c };
                    return new RouteCheck { Error = err.Message, Thoroughfare = err };
                }
            }
            return unreachable;
        }
        return null;
    }

    /// <summary>Marks the rooms (by index) pawns reach from the start cells when a closed room can be entered but not left for another room.</summary>
    private bool[] Flood(IEnumerable<Cell> start, bool[] closed, int rooms)
    {
        var reached = new bool[rooms];
        stamp++;
        var queue = new List<int>();
        foreach (var c in start)
        {
            if (In(c) && room[Index(c)] != 0 && seen[Index(c)] != stamp)
            {
                seen[Index(c)] = stamp;
                queue.Add(Index(c));
            }
        }
        for (var head = 0; head < queue.Count; head++)
        {
            var i = queue[head];
            var here = room[i] - 2;
            if (here >= 0)
            {
                reached[here] = true;
            }
            var c = CellAt(i);
            foreach (var (dx, dz) in Steps)
            {
                var n = new Cell(c.X + dx, c.Z + dz);
                if (!In(n))
                {
                    continue;
                }
                var ni = Index(n);
                if (room[ni] == 0 || seen[ni] == stamp)
                {
                    continue;
                }
                if (here >= 0 && closed[here] && room[ni] - 2 != here)
                {
                    continue;
                }
                seen[ni] = stamp;
                queue.Add(ni);
            }
        }
        return reached;
    }
}

/// <summary>Ports <c>layout_route.go</c>: route checks and traffic over the planned core.</summary>
public static partial class PolicyFunctions
{
    private readonly record struct RouteTrip(PlannedRole From, PlannedRole To, int Weight);

    /// <summary>The room pairs pawns travel between; the empty role is the entrance. Weight is the trip's frequency, the affinity between the two ends: plan scoring weights each walking distance by it.</summary>
    private static RouteTrip[] RouteTrips =>
    [
        new(PlannedBedroom, PlannedDining, 3),
        new(PlannedSuite, PlannedDining, 3),
        new(PlannedKitchen, PlannedFreezer, 3),
        new(default, PlannedFreezer, 2),
        new(PlannedStorage, PlannedWorkshop, 2),
        new(default, PlannedStorage, 1),
        new(PlannedHospital, default, 1),
    ];

    /// <summary>The roles nobody walks through in a room with no RoomCrossing (one that is not a station group); a station group's room carries its own RoomCrossing instead.</summary>
    private static bool NoThroughfare(PlannedRole role) =>
        role == PlannedBedroom || role == PlannedSuite || role == PlannedShelter || role == PlannedPrison
        || role == PlannedKitchen || role == PlannedHospital || role == PlannedLab || role == PlannedThrone;

    /// <summary>The crossing facts of a group: whether it is kept cold (GroupCold), its player-faction beds and whether it needs cleanliness.</summary>
    internal static RoomCrossing CrossingOf(RoomGroup g)
    {
        var beds = 0;
        foreach (var s in g.Stations)
        {
            if (s.Purpose == StationPurpose.Sleep || s.Purpose == StationPurpose.SleepMed || s.Purpose == StationPurpose.SleepBaby)
            {
                beds += s.Count;
            }
        }
        return new RoomCrossing { Cold = GroupCold(g), Clean = g.Requires.Contains(EnvRequirement.Clean), Beds = beds };
    }

    /// <summary>
    /// Whether pawns may cross room r on the way between two others. A room with crossing facts is walked freely when
    /// crossing triggers nothing, when the plan marks it Through if crossing costs, and never when cold; one without
    /// keeps its role's noThroughfare rule.
    /// </summary>
    internal static bool WalkedThrough(PlannedRoom r)
    {
        if (r.Crossing is null)
        {
            return !NoThroughfare(r.Role);
        }
        if (!r.Crossing.Crossable())
        {
            return false;
        }
        return r.Crossing.Mechanisms() == 0 || r.Through;
    }

    /// <summary>Paths every trip over p and returns how many paths cross each cell (for the overlay). A thoroughfare or an unreachable trip is an error. Trips whose rooms the plan lacks are skipped.</summary>
    public static RouteCheck CheckRoutes(LayoutPlan p)
    {
        var (walkMap, rooms) = RouteWalk(p);
        var walk = new RouteGrid(walkMap);
        var cells = TripCells(p, rooms);
        var traffic = new Dictionary<Cell, int>();
        foreach (var trip in RouteTrips)
        {
            var tos = cells(trip.To);
            var froms = cells(trip.From);
            if (p.Entrances.Count == 0 && ((trip.From == PlannedRole.None && tos.Count > 0) || (trip.To == PlannedRole.None && froms.Count > 0)))
            {
                return new RouteCheck { Error = $"no entrances for trip {trip.From} -> {trip.To}" };
            }
            if (tos.Count == 0)
            {
                continue;
            }

            // Every room of the target role is a goal: a ring offers pawns the nearest one.
            var to = tos.SelectMany(g => g).ToList();
            foreach (var from in froms)
            {
                var path = walk.RoutePath(from, to);
                if (path is null)
                {
                    return new RouteCheck { Error = $"no route {trip.From} -> {trip.To}" };
                }
                var ends = new HashSet<int> { walk.At(path[0]), walk.At(path[^1]) };
                foreach (var c in path)
                {
                    traffic[c] = traffic.GetValueOrDefault(c) + 1;
                    var i = walk.At(c);
                    if (i >= 0 && !ends.Contains(i) && !WalkedThrough(rooms[i]))
                    {
                        var err = new ThroughfareError { From = trip.From, To = trip.To, Crossed = rooms[i].Role, Room = rooms[i].Id, Cell = c };
                        return new RouteCheck { Error = err.Message, Thoroughfare = err };
                    }
                }
            }
        }
        if (walk.CheckReachable(p.Entrances, rooms) is { } failed)
        {
            return failed;
        }
        return new RouteCheck { Traffic = traffic };
    }

    /// <summary>A role's trip end cells: the entrances for the empty role, else one single-cell group (the interior centre) per room of that role.</summary>
    internal static Func<PlannedRole, List<List<Cell>>> TripCells(LayoutPlan p, IReadOnlyList<PlannedRoom> rooms) => role =>
    {
        if (role == PlannedRole.None)
        {
            return p.Entrances.Count == 0 ? [] : [[.. p.Entrances]];
        }
        var output = new List<List<Cell>>();
        foreach (var r in rooms)
        {
            if (r.Role == role)
            {
                var b = r.Region.Bounds();
                output.Add([new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2))]);
            }
        }
        return output;
    };

    /// <summary>The cells pawns walk over p, each mapped to its room index (in p.Rooms), -1 for a door cell, with those rooms.</summary>
    internal static (Dictionary<Cell, int> Walk, IReadOnlyList<PlannedRoom> Rooms) RouteWalk(LayoutPlan p)
    {
        var walk = new Dictionary<Cell, int>();
        var rooms = p.Rooms;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Attached())
            {
                continue; // a child is no destination: its parent's door leads through it
            }
            var ins = rooms[i].Region.Bounds();
            for (var x = ins.X; x < ins.X + ins.Width; x++)
            {
                for (var z = ins.Z; z < ins.Z + ins.Height; z++)
                {
                    walk[new Cell(x, z)] = i;
                }
            }
        }
        foreach (var r in rooms)
        {
            if (r.Role == PlannedRole.Airlock)
            {
                walk[r.Vestibule()] = -1;
                walk[r.Door] = -1;
            }
            if (r.Unfenced())
            {
                continue; // no door
            }
            foreach (var c in p.DoorsOf(r))
            {
                walk.TryAdd(c, -1);
            }
        }
        foreach (var l in p.Links)
        {
            walk[l.Cell] = -1;
        }
        foreach (var c in p.Entrances)
        {
            walk.TryAdd(c, -1);
        }
        if (p.Entrances.Count > 0 && p.CoreBounds() is { } fp)
        {
            // A plan is walked over the open ground its entrances stand on: every cell around the rooms that no room or
            // wall holds. The terrain is not read (the embedding sited the rooms; the entrances lie in the map's largest free area).
            var walled = new HashSet<Cell>();
            foreach (var r in rooms)
            {
                var block = RoomWalls(r);
                if (r.Unfenced())
                {
                    // No wall to walk round: the margin a range or ring keeps free of buildings is open ground.
                    block = r.Region.Bounds();
                }
                walled.UnionWith(RectCells(block));
            }
            foreach (var c in RectCells(Pad(fp, 1)))
            {
                if (!walk.ContainsKey(c) && !walled.Contains(c))
                {
                    walk[c] = -1;
                }
            }
        }
        return (walk, rooms);
    }
}
