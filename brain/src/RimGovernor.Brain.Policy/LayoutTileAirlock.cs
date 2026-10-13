namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>layout_tile_airlock.go</c>: tiler airlocks (#2916). On a bad-climate map (the roofed set) a room's exterior
/// door opens a roofed room onto the outdoors. The door is priced two ways in sustained watts: left as a door the
/// pawns use, open for the share of the time their trips hold it, or as an airlock, whose second door and two walls
/// are a one-time cost counted at the layout's own rate (BuildW) and whose two doors are never open at once. The
/// airlock is placed when it is the cheaper total, no veto either way; the ground it needs (the vestibule, its two
/// walls and the outer door, all open ground no other airlock holds) is the only thing that can leave a priced
/// airlock out, and the decision says so. Door traffic is the plan's own trips, walked over the plan (RouteWalk)
/// between its rooms' centres.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// The movements a day through each room's exterior door (its Door, when no link door stands there): every trip
    /// charges its weight to each exterior door on its shortest walk, and so does each room's trip to the outside
    /// (OutsideTrips, walked to the nearest entrance). Trip weights are one way; a visit is a passage in and one out,
    /// so a door's passages a day are twice what the walks charge it (#2947). A trip the plan cannot walk charges
    /// nothing.
    /// </summary>
    internal static Dictionary<int, double> ExteriorDoorMovements(LayoutPlan p)
    {
        var links = new HashSet<Cell>(p.Links.Select(l => l.Cell));
        var output = new Dictionary<int, double>();
        Dictionary<Cell, double>? moves = null;
        foreach (var r in p.Rooms)
        {
            if (r.Unfenced() || links.Contains(r.Door))
            {
                continue;
            }
            moves ??= TripCellMovements(p);
            if (moves.TryGetValue(r.Door, out var m))
            {
                output[r.Id] = 2 * m;
            }
        }
        return output;
    }

    /// <summary>The movements a day through each cell of the plan: every trip charges its weight to each cell on its shortest walk between its rooms' centres (RouteWalk). A trip the plan cannot walk charges nothing.</summary>
    internal static Dictionary<Cell, double> TripCellMovements(LayoutPlan p)
    {
        var output = new Dictionary<Cell, double>();
        if (p.Trips.Count == 0 && p.OutsideTrips.Count == 0)
        {
            return output;
        }
        var (walk, rooms) = RouteWalk(p);
        var centre = new Dictionary<int, Cell>();
        foreach (var r in rooms)
        {
            var b = r.Region.Bounds();
            centre[r.Id] = new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
        }
        var bySource = new Dictionary<int, List<RoomTrip>>();
        var sources = new List<int>();
        foreach (var t in p.Trips)
        {
            if (!centre.ContainsKey(t.A) || !centre.ContainsKey(t.B))
            {
                continue;
            }
            if (!bySource.TryGetValue(t.A, out var list))
            {
                sources.Add(t.A);
                list = [];
                bySource[t.A] = list;
            }
            list.Add(t);
        }
        var outsideW = new Dictionary<int, double>();
        foreach (var t in p.OutsideTrips)
        {
            if (!centre.ContainsKey(t.Room))
            {
                continue;
            }
            sources.Add(t.Room);
            outsideW[t.Room] = outsideW.GetValueOrDefault(t.Room) + t.Weight;
        }
        var entrance = new HashSet<Cell>(p.Entrances);
        sources.Sort();
        foreach (var from in sources.Distinct())
        {
            // Breadth-first from the source, each cell remembering where it came from, in a fixed neighbour order so
            // the path is deterministic.
            var parent = new Dictionary<Cell, Cell> { [centre[from]] = centre[from] };
            var queue = new List<Cell> { centre[from] };
            var nearest = default(Cell);
            var reached = false;
            for (var h = 0; h < queue.Count; h++)
            {
                var c = queue[h];
                if (!reached && entrance.Contains(c))
                {
                    nearest = c;
                    reached = true;
                }
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if (!walk.ContainsKey(n))
                    {
                        continue;
                    }
                    if (parent.TryAdd(n, c))
                    {
                        queue.Add(n);
                    }
                }
            }
            void Charge(Cell at, double weight)
            {
                if (!parent.TryGetValue(at, out var next))
                {
                    return;
                }
                while (true)
                {
                    output[at] = output.GetValueOrDefault(at) + weight;
                    if (next == at)
                    {
                        return;
                    }
                    at = next;
                    next = parent[at];
                }
            }
            foreach (var t in bySource.GetValueOrDefault(from) ?? [])
            {
                Charge(centre[t.B], t.Weight);
            }
            if (reached && outsideW.GetValueOrDefault(from) > 0)
            {
                Charge(nearest, outsideW[from]);
            }
        }
        return output;
    }

    /// <summary>Whether every floor cell of r is in the roofed set.</summary>
    internal static bool RoofedRoom(Region roofed, PlannedRoom r) => r.Region.Cells().All(roofed.Contains);
}

internal sealed partial class Tiler
{
    /// <summary>
    /// Attaches an airlock to every roofed room's exterior door when the airlock is the cheaper opening in sustained
    /// watts, and reports whether it placed any. The airlocks follow the group rooms in plan order, each a child room
    /// (Parent set) with the next room id.
    /// </summary>
    internal bool PlaceAirlocks()
    {
        var p = Out.Plan;
        if (p.Roofed.Empty())
        {
            NoteAirlock(-1, ReasonTileAirlockTemperate, TilePlace, 0, 0);
            return false;
        }
        var known = In.OutdoorCurve.TryGetValue(out var curve);
        var m = In.Catalog.Climate;
        if (!known || !m.Valid() || !FinitePositive(In.SteelValue))
        {
            NoteAirlock(-1, ReasonTileAirlockUnpriced, TileUnsatisfied, 0, 0);
            return false;
        }
        var movements = ExteriorDoorMovements(p);
        var claimed = new HashSet<Cell>();
        var placed = false;
        for (var i = 0; i < Rooms.Count; i++)
        {
            var room = p.Rooms[i];
            if (ExtDoor[i] is null || room.Unfenced() || !RoofedRoom(p.Roofed, room))
            {
                continue;
            }
            var g = In.Groups[Rooms[i].Group];
            var band = ComfortBand();
            if (GroupCold(g) && In.FullRotRateC > 0)
            {
                band = new Band { MinC = double.NegativeInfinity, MaxC = In.FullRotRateC };
            }
            List<ClimateOption> options;
            try
            {
                options = ClimateOptions(m, DefaultSiteTypeWeights(), In.SteelValue, curve!, new ClimateRoom { Cells = room.Region.Area(), Band = band },
                    new ClimateSide { Outdoors = true }, movements.GetValueOrDefault(room.Id), [Opening.Door, Opening.Airlock]);
            }
            catch (DomainException)
            {
                NoteAirlock(g.Id, ReasonTileAirlockUnpriced, TileUnsatisfied, 0, 0);
                continue;
            }
            var door = options[0].TotalW;
            var airlock = options[1].TotalW;
            if (airlock >= door)
            {
                NoteAirlock(g.Id, ReasonTileAirlockNotPaid, TileUnsatisfied, door, airlock);
                continue;
            }
            var rot = room.DoorRot!.Value;
            var child = AttachAirlock(room.Door, rot);
            var ground = new List<Cell>(child.AirlockWalls()) { child.Vestibule(), child.Door };
            if (ground.Any(c => !Free.Contains(c) || claimed.Contains(c)) || !Free.Contains(Outside(child.Door, rot)))
            {
                NoteAirlock(g.Id, ReasonTileAirlockNoGround, TileUnsatisfied, door, airlock);
                continue;
            }
            foreach (var c in ground)
            {
                claimed.Add(c);
            }
            child = child with { Id = Out.Plan.NextRoomId, Parent = room.Id };
            Out.Plan = Out.Plan with { NextRoomId = Out.Plan.NextRoomId + 1, Rooms = [.. Out.Plan.Rooms, child] };
            NoteAirlock(g.Id, ReasonTileAirlock, TilePlace, door, airlock);
            placed = true;
        }
        return placed;
    }

    internal void NoteAirlock(int group, string reason, string verdict, double doorW, double airlockW) =>
        Out.Decisions.Add(new TileDecision { Group = group, Other = -1, Verdict = verdict, Reason = reason, DoorW = doorW, AirlockW = airlockW });
}
