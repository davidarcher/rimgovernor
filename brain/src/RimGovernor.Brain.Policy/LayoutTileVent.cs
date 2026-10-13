namespace RimGovernor.Brain.Policy;

/// <summary>A vent the tiler placed: its cell, its room and the room beside it (-1 for the outdoors) by room index.</summary>
internal readonly record struct TileVent(Cell Cell, int Room, int Other, string Reason);

/// <summary>A way a room could be vented: the wall cells that lead to one side. Other is the neighbour's room index, -1 for the outdoors.</summary>
internal sealed record VentChannel(List<Cell> Cells, int Other);

/// <summary>
/// Ports <c>layout_tile_vent.go</c>: vents in the tiled plan (#2915). A vent is a wall cell with a room cell on one
/// side and the outdoors, or a neighbouring room, on the other; it is a PlannedSite of kind NodeVent, one cell,
/// standing in the wall ring. The tiler picks them after the doors are final (KeepRoutes), from the wall cells no
/// door, link or entrance holds: to the outdoors (inside, wall and outside in line, the outside cell open ground), or
/// room to room (a cell of the shared wall beside the link door). Each room is priced in sustained watts by the
/// climate model: the room's own stations' heat, its band, the outdoor curve, and a neighbour held at the heater's
/// default target (or, for a cold room, at the top of its band). The vents it would take are priced with their build
/// cost as watts and the heaters and coolers each room then needs built against the room with every door closed, with
/// its exterior door held open, and with each link door held open. A vent that is not the cheapest is not placed, and
/// the decision says so. A vent whose research is missing, or whose availability is unknown, is an absent option. The
/// cooler and the vents want the same outside wall cells, so the vents leave as many outside cells as the room's group
/// has wall stations.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>NodeVent is a vent standing in a room's wall.</summary>
    public static NodeKind NodeVent => NodeKind.Vent;

    /// <summary>Verdict of a vent decision: the option was priced and not wanted.</summary>
    public const string TileAbsent = "absent";

    public const string ReasonTileVent = "tile_vent";
    public const string ReasonTileVentLink = "tile_vent_link";
    public const string ReasonTileVentUnavailable = "tile_vent_unavailable";
    public const string ReasonTileVentClimateUnknown = "tile_vent_climate_unknown";
    public const string ReasonTileVentNotCheaper = "tile_vent_not_cheaper";
}

/// <summary>Prices rooms in the tiler's climate: a room's mean power and plants with its doors closed or one held open, the vents placed so far and the new vents asked of it.</summary>
internal sealed class VentPricer(Tiler t, ClimateModel m, IReadOnlyList<double> curve)
{
    internal Exception? Failed { get; set; }
    private readonly Dictionary<string, double> memo = [];

    internal Band BandOf(int r) => t.VentBand(t.In.Groups[t.Rooms[r].Group]) is { } b ? b : m.Living;

    /// <summary>Room r as a neighbour: held where its band holds it.</summary>
    internal ClimateSide Side(int r) => new() { HeldC = t.HeldAt(BandOf(r)), Cells = t.RoomCells(r) };

    /// <summary>
    /// Room r's mean power with the heaters and coolers its load needs built (PlantsW). open is the door held open
    /// (-2 none, -1 the exterior door, else the neighbour's room index); extra is the new vents toward each room (-1
    /// the outdoors).
    /// </summary>
    internal double Watts(int r, int open, IReadOnlyDictionary<int, int> extra)
    {
        var key = $"{r} {open} [{string.Join(' ', extra.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value}"))}]";
        if (memo.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var links = new List<ClimateLink>();
        var room = new ClimateRoom { Cells = t.RoomCells(r), Band = BandOf(r), Heat = EquatableArray.From(t.In.Catalog.GroupHeat(t.In.Groups[t.Rooms[r].Group])) };
        ClimateLink Door(int who, ClimateSide side) => new() { Opening = who == open ? Opening.DoorOpen : Opening.DoorClosed, Side = side };
        var outdoors = new ClimateSide { Outdoors = true };
        if (t.ExtDoor[r] is not null)
        {
            links.Add(Door(-1, outdoors));
        }
        foreach (var j in t.Linked(r))
        {
            links.Add(Door(j, Side(j)));
        }
        void Vent(ClimateSide side) => links.Add(new ClimateLink { Opening = Opening.Vent, Side = side });
        foreach (var v in t.Vents)
        {
            if (v.Room == r && v.Other < 0)
            {
                Vent(outdoors);
            }
            else if (v.Room == r)
            {
                Vent(Side(v.Other));
            }
            else if (v.Other == r)
            {
                Vent(Side(v.Room));
            }
        }
        foreach (var (to, count) in extra.OrderBy(e => e.Key))
        {
            for (var i = 0; i < count; i++)
            {
                Vent(to < 0 ? outdoors : Side(to));
            }
        }
        room = room with { Links = EquatableArray.From(links) };
        ClimateWatts w;
        try
        {
            w = ClimateLoad(m, curve, room, 0);
        }
        catch (DomainException e)
        {
            Failed = e;
            return double.PositiveInfinity;
        }
        var cost = w.MeanW + m.PlantsW(DefaultSiteTypeWeights(), t.In.SteelValue, w);
        memo[key] = cost;
        return cost;
    }
}

internal sealed partial class Tiler
{
    /// <summary>The band a room is held in: under the full-rot temperature when its group stores cold, else the colonists' comfortable range. Null when the full-rot temperature is unknown.</summary>
    internal Band? VentBand(RoomGroup g)
    {
        if (GroupCold(g))
        {
            if (In.FullRotRateC <= 0)
            {
                return null;
            }
            return new Band { MinC = double.NegativeInfinity, MaxC = In.FullRotRateC };
        }
        return In.Catalog.Climate.Living;
    }

    /// <summary>The temperature a room is held at, as a neighbour: the top of a band with no floor, else the heater's default target.</summary>
    internal double HeldAt(Band b) => double.IsNegativeInfinity(b.MinC) ? b.MaxC : In.Catalog.Climate.HeldC;

    internal int RoomCells(int i) => Rooms[i].Rect.Width * Rooms[i].Rect.Height;

    /// <summary>The wall stations the room's group will set into its ring.</summary>
    internal int WallDemand(RoomGroup g)
    {
        var n = 0;
        foreach (var s in g.Stations)
        {
            var (st, ok) = In.Catalog.Station(s.Def);
            if (ok && st.Access == StationAccess.Wall)
            {
                n += s.Count;
            }
        }
        return n;
    }

    /// <summary>The wall cells of room i that lead outdoors: inside, wall and outside in line, the outside cell open ground, the cell no door of the room and no vent holds.</summary>
    internal List<Cell> VentCandidates(int i, HashSet<Cell> used)
    {
        var rc = Rooms[i].Rect;
        var doors = DoorsOf(i);
        var output = new List<Cell>();
        void Add(Cell wall, Cell outside)
        {
            if (Free.Contains(outside) && !used.Contains(wall) && !doors.Contains(wall))
            {
                output.Add(wall);
            }
        }
        for (var x = rc.X; x < rc.X + rc.Width; x++)
        {
            Add(new Cell(x, rc.Z - 1), new Cell(x, rc.Z - 2));
            Add(new Cell(x, rc.Z + rc.Height), new Cell(x, rc.Z + rc.Height + 1));
        }
        for (var z = rc.Z; z < rc.Z + rc.Height; z++)
        {
            Add(new Cell(rc.X - 1, z), new Cell(rc.X - 2, z));
            Add(new Cell(rc.X + rc.Width, z), new Cell(rc.X + rc.Width + 1, z));
        }
        SortCells(output);
        return output;
    }

    /// <summary>The cells of the wall room pair k shares that stand beside its link door and are neither a door nor a vent.</summary>
    internal List<Cell> LinkCandidates((int, int) k, HashSet<Cell> used)
    {
        var door = Links[k];
        var doors = DoorsOf(k.Item1).Concat(DoorsOf(k.Item2)).ToList();
        var output = new List<Cell>();
        foreach (var c in Adj[k])
        {
            if (Math.Abs(c.X - door.X) + Math.Abs(c.Z - door.Z) == 1 && !used.Contains(c) && !doors.Contains(c))
            {
                output.Add(c);
            }
        }
        SortCells(output);
        return output;
    }

    /// <summary>The rooms room r has a link door to, in room order.</summary>
    internal List<int> Linked(int r)
    {
        var output = new List<int>();
        foreach (var k in Links.Keys)
        {
            if (k.Item1 == r || k.Item2 == r)
            {
                output.Add(k.Item1 + k.Item2 - r);
            }
        }
        output.Sort();
        return output;
    }

    /// <summary>Places the vents of every fenced room.</summary>
    internal void ChooseVents()
    {
        var m = In.Catalog.Climate;
        var known = In.OutdoorCurve.TryGetValue(out var curve);
        if (!known || !m.HasLiving() || !FinitePositive(In.SteelValue))
        {
            Note(-1, -1, TileUnsatisfied, ReasonTileVentClimateUnknown);
            return;
        }
        In.VentAvailable.TryGetValue(out var available);
        var ventW = BuildW(DefaultSiteTypeWeights(), m.Build.Vent, In.SteelValue);
        var p = new VentPricer(this, m, curve!);
        var used = new HashSet<Cell>();
        for (var i = 0; i < Rooms.Count; i++)
        {
            var r = Rooms[i];
            if (Open(i) || Plan.Rooms[i].Role.IsOutdoor())
            {
                continue;
            }
            var g = In.Groups[r.Group];
            if (VentBand(g) is null)
            {
                Note(r.Group, -1, TileUnsatisfied, ReasonTileVentClimateUnknown);
                continue;
            }

            // The channels: the outdoors, then each link door's neighbour.
            var channels = new List<VentChannel>();
            var outdoorCells = VentCandidates(i, used);
            if (outdoorCells.Count > 0)
            {
                outdoorCells = outdoorCells.GetRange(0, Math.Max(0, outdoorCells.Count - WallDemand(g)));
                if (outdoorCells.Count > 0)
                {
                    channels.Add(new VentChannel(outdoorCells, -1));
                }
            }
            var neighbours = Linked(i);
            foreach (var j in neighbours)
            {
                var cells = LinkCandidates(PairOf(i, j), used);
                if (cells.Count > 0)
                {
                    channels.Add(new VentChannel(cells, j));
                }
            }
            if (channels.Count == 0)
            {
                continue;
            }

            // total is what the room and the rooms beside it pay in watts, with n new vents on each channel and a door
            // held open: a vent between two rooms loads both, and a room beside it that must be cooled for the heat
            // the vent brings it is no sink.
            double Total(int[] n, int open)
            {
                var extra = new Dictionary<int, int>();
                var vents = 0;
                for (var c = 0; c < n.Length; c++)
                {
                    if (n[c] > 0)
                    {
                        extra[channels[c].Other] = n[c];
                    }
                    vents += n[c];
                }
                var w = p.Watts(i, open, extra) + (vents * ventW);
                foreach (var j in neighbours)
                {
                    var oj = open == j ? i : -2;
                    w += p.Watts(j, oj, new Dictionary<int, int> { [i] = extra.GetValueOrDefault(j) });
                }
                return w;
            }
            var none = new int[channels.Count];
            var doorsOnly = Total(none, -2);
            if (ExtDoor[i] is not null)
            {
                doorsOnly = Math.Min(doorsOnly, Total(none, -1));
            }
            foreach (var j in neighbours)
            {
                doorsOnly = Math.Min(doorsOnly, Total(none, j));
            }

            // Coordinate descent over the vent counts: a few passes settle it, as each channel's cost falls then rises
            // with its count.
            var n2 = (int[])none.Clone();
            for (var pass = 0; pass < 3; pass++)
            {
                var changed = false;
                for (var c = 0; c < channels.Count; c++)
                {
                    var held = n2[c];
                    var best = held;
                    var bestCost = Total(n2, -2);
                    var prev = double.PositiveInfinity;
                    for (var count = 0; count <= channels[c].Cells.Count; count++)
                    {
                        n2[c] = count;
                        var cost = Total(n2, -2);
                        if (cost < bestCost - 1e-9)
                        {
                            best = count;
                            bestCost = cost;
                        }
                        if (count > 0 && cost > prev)
                        {
                            break;
                        }
                        prev = cost;
                    }
                    changed = changed || best != held;
                    n2[c] = best;
                }
                if (!changed)
                {
                    break;
                }
            }
            if (p.Failed is not null)
            {
                Note(r.Group, -1, TileUnsatisfied, ReasonTileVentClimateUnknown);
                p.Failed = null;
                continue;
            }
            var wanted = n2.Sum();
            if (wanted == 0 || Total(n2, -2) >= doorsOnly - 1e-9)
            {
                Note(r.Group, -1, TileAbsent, ReasonTileVentNotCheaper);
                continue;
            }
            if (!available)
            {
                Note(r.Group, -1, TileUnsatisfied, ReasonTileVentUnavailable);
                continue;
            }
            for (var c = 0; c < n2.Length; c++)
            {
                foreach (var cell in channels[c].Cells.Take(n2[c]))
                {
                    used.Add(cell);
                    var reason = ReasonTileVent;
                    var other = -1;
                    if (channels[c].Other >= 0)
                    {
                        reason = ReasonTileVentLink;
                        other = Rooms[channels[c].Other].Group;
                    }
                    Vents.Add(new TileVent(cell, i, channels[c].Other, reason));
                    Note(r.Group, other, TilePlace, reason);
                }
            }
        }
    }

    /// <summary>The placed vents as plan nodes, in cell order.</summary>
    internal List<PlannedSite> VentSites()
    {
        var vents = Vents.ToList();
        vents.Sort((a, b) => CellLess(a.Cell, b.Cell) ? -1 : CellLess(b.Cell, a.Cell) ? 1 : 0);
        var output = new List<PlannedSite>(vents.Count);
        foreach (var v in vents)
        {
            var site = NewSite($"vent_{v.Cell.X}_{v.Cell.Z}", NodeVent, RectRegion(new Rectangle { X = v.Cell.X, Z = v.Cell.Z, Width = 1, Height = 1 }), v.Reason);
            var (rot, _) = DoorSide(Rooms[v.Room].Rect, v.Cell);
            output.Add(site with { Rotation = rot });
        }
        return output;
    }
}
