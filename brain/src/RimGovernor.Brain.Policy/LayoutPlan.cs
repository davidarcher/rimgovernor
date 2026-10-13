namespace RimGovernor.Brain.Policy;

/// <summary>A door in the wall two rooms share: A and B are their room ids (A below B) and Cell the wall cell, which lies on both rooms' walls.</summary>
public readonly record struct RoomLink(int A, int B, Cell Cell);

/// <summary>One planned room.</summary>
public sealed partial record PlannedRoom
{
    /// <summary>Names the room for as long as the plan lives: handed out by LayoutPlan.NextRoomId, never reused, kept through reshape, shrink and replans that pin the room. Never zero in a plan a store holds.</summary>
    public int Id { get; init; }

    /// <summary>The Id of the room this one supersedes; 0 for none.</summary>
    public int Replaces { get; init; }

    public PlannedRole Role { get; init; }

    /// <summary>The room's floor, walls excluded.</summary>
    public Region Region { get; init; } = new();

    /// <summary>The primary door: the one furniture frames and rock planning use.</summary>
    public Cell Door { get; init; }

    /// <summary>The door's side; null where Go's empty rotation stood (an unfenced room has no door).</summary>
    public Rotation? DoorRot { get; init; }

    /// <summary>What walking through the room costs, read from the station group that became it (RoomCrossing); null for a room that is not a station group, which keeps its role's noThroughfare rule.</summary>
    public RoomCrossing? Crossing { get; init; }

    /// <summary>Marks a room the plan lets pawns walk through to reach other rooms although crossing it costs something (MayBeThrough).</summary>
    public bool Through { get; init; }

    /// <summary>A room mined out of natural rock.</summary>
    public bool Dug { get; init; }

    /// <summary>An open room: a fence and a gate ring (RingDefs, the pen) or none (Unfenced), no roof and no floor owed (the animal pen).</summary>
    public bool Outdoor { get; init; }

    /// <summary>The direction an unfenced training range's dummies lie from its stands (Unfenced); null for every other room.</summary>
    public Rotation? Facing { get; init; }

    /// <summary>The marker count of a sparring ring (RingSlot orders the slots); 0 for every other room.</summary>
    public int Markers { get; init; }

    /// <summary>The Id of the room this one is attached to: an airlock or porch child; 0 for every room that stands on its own. A child owns no ring (Unfenced): its parent's ring takes its walls and doors.</summary>
    public int Parent { get; init; }

    /// <summary>Maps the definition of a worked bench of the room to the work chair the comfort pricing chose for it (ChooseComfort, #2978); the furnisher stands one on each such bench's interaction cell. Absent: no chair is chosen or priced.</summary>
    public IReadOnlyDictionary<string, string> WorkChairs { get; init; } = new Dictionary<string, string>();

    /// <summary>The ground an attached child occupies beyond its parent's ring: the airlock's vestibule, walls and outer door, or the porch strip.</summary>
    internal Rectangle AttachedGround() => Role == PlannedRole.Airlock ? this.AirlockGround() : PorchGround();

    /// <summary>A child room that hangs off a parent: an airlock or porch.</summary>
    internal bool Attached() => Role == PlannedRole.Airlock || Role == PlannedPorch;

    /// <summary>Whether two rooms are identical, the comparison Region and Crossing rule out for value equality.</summary>
    public bool Same(PlannedRoom o) =>
        Id == o.Id
        && Replaces == o.Replaces
        && Role == o.Role
        && Through == o.Through
        && Equals(Crossing, o.Crossing)
        && Region.Bounds() == o.Region.Bounds()
        && Door == o.Door
        && DoorRot == o.DoorRot
        && Dug == o.Dug
        && Outdoor == o.Outdoor
        && Facing == o.Facing
        && Markers == o.Markers
        && Parent == o.Parent;
}

/// <summary>The cells X..X+Length-1 on row Z.</summary>
public readonly record struct RowRun(int Z, int X, int Length);

/// <summary>One zone as row runs over the whole map.</summary>
public sealed record LayoutZone
{
    public ZoneKind Kind { get; init; }
    public IReadOnlyList<RowRun> Runs { get; init; } = [];

    /// <summary>Marks the mining zone over ore-bearing rock.</summary>
    public bool Ore { get; init; }
}

/// <summary>One reserved site.</summary>
public sealed record LayoutReservation
{
    public ReservationKind Kind { get; init; }
    public Region Area { get; init; } = new();

    /// <summary>The side a walled reservation's door faces (the incinerator).</summary>
    public Rotation? Facing { get; init; }

    /// <summary>The race of the herd whose unit a pen, barn or vet room belongs to; empty for the misc unit and for plans saved before it.</summary>
    public string Herd { get; init; } = "";

    /// <summary>Whether both reservations are the same site.</summary>
    public bool SameSite(LayoutReservation o) => Kind == o.Kind && Facing == o.Facing && Herd == o.Herd && Area.Equal(o.Area);
}

/// <summary>The v2 colony layout: concrete rooms, whole-map zones and reserved infrastructure sites.</summary>
public sealed partial record LayoutPlan
{
    /// <summary>The cells traffic enters the base from: the outside cell beside each exterior door on a tiled plan. Empty until the plan is grown.</summary>
    public IReadOnlyList<Cell> Entrances { get; init; } = [];

    /// <summary>The door graph between rooms: one door in the wall two rooms share, by room id.</summary>
    public IReadOnlyList<RoomLink> Links { get; init; } = [];

    /// <summary>The power conduit paths the planner lays, one per node joined to the net.</summary>
    public IReadOnlyList<ConduitLink> Conduits { get; init; } = [];

    /// <summary>The room each station group owes the plan, one role per group, set only by a tiled plan. The hard tier's Missing term counts a room per entry; empty keeps defaultOwedRooms, once each.</summary>
    public IReadOnlyList<PlannedRole> Owed { get; init; } = [];

    /// <summary>The pawn trips between this plan's rooms with their daily frequency. When set, plan scoring walks them instead of the role-pair trip table.</summary>
    public IReadOnlyList<RoomTrip> Trips { get; init; } = [];

    /// <summary>The rooms' trips to the outside, walked to the nearest entrance; scoring does not read them.</summary>
    public IReadOnlyList<OutsideTrip> OutsideTrips { get; init; } = [];

    public IReadOnlyList<PlannedRoom> Rooms { get; init; } = [];
    public IReadOnlyList<LayoutZone> Zones { get; init; } = [];
    public IReadOnlyList<LayoutReservation> Reservations { get; init; } = [];

    /// <summary>The defense nodes PlanPerimeter traces: ring, gates, killbox, approach, fence, pumps, mortar and cover-clear band.</summary>
    public IReadOnlyList<PlannedSite> Sites { get; init; } = [];

    /// <summary>The walled footprint of each room the plan retired while its building stands: planned-ground clearance demolishes it, then the entry is dropped.</summary>
    public IReadOnlyList<Region> RetiredGround { get; init; } = [];

    /// <summary>The cells the plan roofs on a map whose outdoor temperature is not good for pawns: every room except the outdoor roles. Only a tiled plan holds it.</summary>
    public Region Roofed { get; init; } = new();

    /// <summary>The lamps the plan places in each lit room by room id: standing or wall, priced against the dark they light. The lighting round builds them where a bench reads dark; a room the plan found darkness cheaper for has none. Only a tiled plan holds them.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PlannedLamp>> Lamps { get; init; } = new Dictionary<int, IReadOnlyList<PlannedLamp>>();

    /// <summary>Latches the map's climate when the plan is derived: the seasonal curve dips below ColdMapBelowC. The shelter is sized for it, so a replan never resizes or re-sites the room.</summary>
    public bool Cold { get; init; }

    /// <summary>Latches the other end of the climate the same way: the seasonal curve peaks above HotEnter, so the shelter holds a floor slot for a passive cooler.</summary>
    public bool Hot { get; init; }

    /// <summary>The counter room ids come from: the next id handed out, latched in the plan so an id is never reused after its room leaves.</summary>
    public int NextRoomId { get; init; }

    /// <summary>
    /// Hands every room without an id the next one from NextRoomId, in plan order. The counter first moves past
    /// every id the plan already holds, so ids stay unique and a dropped room's id is not handed out again.
    /// </summary>
    public LayoutPlan WithRoomIDs()
    {
        var next = Math.Max(NextRoomId, 1);
        foreach (var r in Rooms)
        {
            next = Math.Max(next, Math.Max(r.Id + 1, r.Replaces + 1));
        }
        var rooms = Rooms.ToList();
        next = IdRooms(rooms, next);
        return this with { Rooms = rooms, NextRoomId = next };
    }

    /// <summary>Hands each room without an id the next from next, in place, and returns the counter. An attached child (airlock, porch) sited with no Parent is the parent's: the room listed just before it.</summary>
    internal static int IdRooms(List<PlannedRoom> rooms, int next)
    {
        var last = 0;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Id == 0)
            {
                rooms[i] = rooms[i] with { Id = next };
                next++;
            }
            if (!rooms[i].Attached())
            {
                last = rooms[i].Id;
            }
            else if (rooms[i].Parent == 0)
            {
                rooms[i] = rooms[i] with { Parent = last };
            }
        }
        return next;
    }

    /// <summary>The interior centre of the first room for want that free accepts (every one when free is null), in plan order. A full role falls back to reserve rooms; null means the plan holds no slot.</summary>
    public Cell? Anchor(PlannedRole want, Func<Rectangle, bool>? free)
    {
        foreach (var role in new[] { want, PlannedReserve })
        {
            foreach (var r in Rooms)
            {
                if (r.Role == role && (free is null || free(r.Region.Bounds())))
                {
                    var b = r.Region.Bounds();
                    return new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
                }
            }
        }
        return null;
    }

    /// <summary>The cell the base clusters around: the first storeroom's interior centre, else the centre of the footprint of every room wall. Null means an empty plan.</summary>
    public Cell? Core()
    {
        foreach (var r in Rooms)
        {
            if (r.Role == PlannedStorage)
            {
                var b = r.Region.Bounds();
                return new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
            }
        }
        return Center();
    }

    /// <summary>The middle of the plan's core: the bounding box of its room walls. Null for a plan with no rooms.</summary>
    public Cell? Center()
    {
        var fp = CoreBounds();
        if (fp is null)
        {
            return null;
        }
        return new Cell(fp.Value.X + (fp.Value.Width / 2), fp.Value.Z + (fp.Value.Height / 2));
    }

    /// <summary>The bounding box of the plan's room walls; null for a plan with no rooms.</summary>
    public Rectangle? CoreBounds()
    {
        var fp = new Rectangle();
        foreach (var r in Rooms)
        {
            fp = UnionRect(fp, RoomWalls(r));
        }
        return fp.Width != 0 ? fp : null;
    }

    /// <summary>Whether the plan is one a store may persist: at least one room, non-empty rectangles and runs, and a real door rotation.</summary>
    public bool Valid()
    {
        if (Rooms.Count == 0)
        {
            return false;
        }
        foreach (var c in Entrances)
        {
            if (!BesideExteriorDoor(c))
            {
                return false;
            }
        }
        var ids = new HashSet<int>();
        foreach (var r in Rooms)
        {
            if (r.Role == PlannedRole.None || r.Region.Empty())
            {
                return false;
            }
            if (r.Id < 1 || ids.Contains(r.Id) || r.Id >= NextRoomId || r.Replaces == r.Id || r.Parent == r.Id)
            {
                return false;
            }
            if (r.Attached() != (r.Parent != 0))
            {
                return false;
            }
            ids.Add(r.Id);
            if (r.Through && !r.MayBeThrough())
            {
                return false;
            }
            if (r.DoorRot is null && !r.Unfenced())
            {
                // an unfenced room has no door
                return false;
            }
        }
        var rooms = new Dictionary<int, Rectangle>();
        foreach (var r in Rooms)
        {
            rooms[r.Id] = r.Region.Bounds();
        }
        var seen = new HashSet<RoomLink>();
        foreach (var l in Links)
        {
            if (!rooms.TryGetValue(l.A, out var a) || !rooms.TryGetValue(l.B, out var b) || l.A >= l.B || seen.Contains(l) || !InWall(a, l.Cell) || !InWall(b, l.Cell))
            {
                return false;
            }
            seen.Add(l);
        }
        if (RoofedOutdoors())
        {
            return false;
        }
        if (!this.ValidConduits(rooms))
        {
            return false;
        }
        foreach (var t in Trips)
        {
            if (!rooms.ContainsKey(t.A) || t.A >= t.B || t.Weight <= 0)
            {
                return false;
            }
            if (!rooms.ContainsKey(t.B))
            {
                return false;
            }
        }
        foreach (var z in Zones)
        {
            if (z.Kind == ZoneKind.None)
            {
                return false;
            }
            foreach (var run in z.Runs)
            {
                if (run.Length < 1)
                {
                    return false;
                }
            }
        }
        foreach (var g in RetiredGround)
        {
            if (g.Empty())
            {
                return false;
            }
        }
        foreach (var r in Reservations)
        {
            if (r.Kind == ReservationKind.None || r.Area.Empty())
            {
                return false;
            }
        }
        var siteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Sites)
        {
            if (s.Kind == NodeKind.None || s.Node == "" || siteIds.Contains(s.Node) || s.Footprint.Empty() || (s.Kind == NodeRing && s.Ring is null))
            {
                return false;
            }
            siteIds.Add(s.Node);
            if (s.Kind == NodeVent && !ValidVent(s))
            {
                return false;
            }
        }
        foreach (var s in Sites)
        {
            foreach (var id in s.ReplacesSites)
            {
                if (!siteIds.Contains(id) || id == s.Node)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>A vent that stands in a room's wall: one cell of a fenced room's wall ring, a corner excluded, that no door, link or entrance holds.</summary>
    private bool ValidVent(PlannedSite s)
    {
        var cells = s.Footprint.Cells();
        if (cells.Count != 1)
        {
            return false;
        }
        var c = cells[0];
        if (Entrances.Contains(c) || Links.Any(l => l.Cell == c))
        {
            return false;
        }
        var inRing = false;
        foreach (var r in Rooms)
        {
            if (r.Unfenced())
            {
                continue;
            }
            if (DoorsOf(r).Contains(c))
            {
                return false;
            }
            inRing = inRing || InWall(r.Region.Bounds(), c);
        }
        return inRing;
    }

    /// <summary>c as the outside cell of a room's own door: it touches the room's Door from outside the room.</summary>
    private bool BesideExteriorDoor(Cell c)
    {
        foreach (var r in Rooms)
        {
            if (r.Unfenced() || r.Region.Contains(c))
            {
                continue;
            }
            if (Abs32(r.Door.X - c.X) + Abs32(r.Door.Z - c.Z) == 1)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The cells of the doors the door graph gives room id.</summary>
    public List<Cell> LinkCells(int id) => [.. Links.Where(l => l.A == id || l.B == id).Select(l => l.Cell)];

    /// <summary>The cells of the link doors room r holds in walls it shares with a neighbour: the door graph's links. Herd rooms (barn, pen) are reservations without room ids and own no link, so they give none.</summary>
    public List<Cell> LinksOf(PlannedRoom r) => r.Id == 0 ? [] : LinkCells(r.Id);

    /// <summary>The cells of every door of room r: Door, then its links, each cell once.</summary>
    public List<Cell> DoorsOf(PlannedRoom r)
    {
        var doors = new List<Cell> { r.Door };
        foreach (var c in LinksOf(r))
        {
            if (!doors.Contains(c))
            {
                doors.Add(c);
            }
        }
        return doors;
    }

    /// <summary>The cell of r's link door that opens into a room of role, else its first link; null when r holds none. The freezer's door into the kitchen and the butchery's into the freezer pick their link this way, where a tiled room may hold several.</summary>
    internal Cell? LinkInto(PlannedRoom r, PlannedRole role)
    {
        var links = LinksOf(r);
        foreach (var c in links)
        {
            foreach (var o in Rooms)
            {
                if (o.Id != r.Id && o.Role == role && InWall(o.Region.Bounds(), c))
                {
                    return c;
                }
            }
        }
        return links.Count == 0 ? null : links[0];
    }

    /// <summary>The bounding rectangle of what the plan builds: every room with its walls and every reserved site. The whole-map zones are left out; null means the plan holds none of these.</summary>
    public Rectangle? Extent()
    {
        var rects = new List<Rectangle>();
        foreach (var r in Rooms)
        {
            var b = r.Region.Bounds();
            rects.Add(new Rectangle { X = b.X - 1, Z = b.Z - 1, Width = b.Width + 2, Height = b.Height + 2 });
        }
        foreach (var r in Reservations)
        {
            rects.Add(r.Area.Bounds());
        }
        foreach (var s in Sites)
        {
            rects.Add(s.Footprint.Bounds());
        }
        return RectUnion(rects.ToArray());
    }

    /// <summary>The interior centre of the free planned room of role want nearest to point to (squared distance, plan order on ties); free filters out occupied rooms (every room when null). With no free room of that role it falls back to the nearest free reserve room; null means the plan holds no slot.</summary>
    public Cell? NearestAnchor(PlannedRole want, Cell to, Func<Rectangle, bool>? free)
    {
        foreach (var role in new[] { want, PlannedReserve })
        {
            Cell best = default;
            long bestD = 0;
            var found = false;
            foreach (var r in Rooms)
            {
                if (r.Role != role || (free is not null && !free(r.Region.Bounds())))
                {
                    continue;
                }
                var b = r.Region.Bounds();
                var c = new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
                long dx = c.X - to.X;
                long dz = c.Z - to.Z;
                var d = (dx * dx) + (dz * dz);
                if (!found || d < bestD)
                {
                    (best, bestD, found) = (c, d, true);
                }
            }
            if (found)
            {
                return best;
            }
        }
        return null;
    }

    /// <summary>The middle of the first field-zone run whose middle cell free accepts (every run when null); null means no such run.</summary>
    public Cell? FieldAnchor(Func<Rectangle, bool>? free)
    {
        foreach (var z in Zones)
        {
            if (z.Kind != ZoneField)
            {
                continue;
            }
            foreach (var run in z.Runs)
            {
                var c = new Cell(run.X + (run.Length / 2), run.Z);
                if (free is null || free(new Rectangle { X = c.X, Z = c.Z, Width = 1, Height = 1 }))
                {
                    return c;
                }
            }
        }
        return null;
    }
}

public static partial class PolicyFunctions
{
    public static ZoneKind ZoneField => ZoneKind.Field;
    public static ZoneKind ZonePasture => ZoneKind.Pasture;
    public static ZoneKind ZoneMining => ZoneKind.Mining;
    public static ZoneKind ZoneWood => ZoneKind.Wood;
    public static ZoneKind ZoneNoGo => ZoneKind.NoGo;

    public static ReservationKind ReserveBatteryRoom => ReservationKind.BatteryRoom;
    public static ReservationKind ReserveGeothermal => ReservationKind.Geothermal;

    /// <summary>The bounding rectangle of the non-empty rects; null when there are none.</summary>
    public static Rectangle? RectUnion(params Rectangle[] rects)
    {
        var found = false;
        var result = new Rectangle();
        foreach (var r in rects)
        {
            if (r.Width < 1 || r.Height < 1)
            {
                continue;
            }
            if (!found)
            {
                (result, found) = (r, true);
                continue;
            }
            var minX = Math.Min(result.X, r.X);
            var minZ = Math.Min(result.Z, r.Z);
            var maxX = Math.Max(result.X + result.Width, r.X + r.Width);
            var maxZ = Math.Max(result.Z + result.Height, r.Z + r.Height);
            result = new Rectangle { X = minX, Z = minZ, Width = maxX - minX, Height = maxZ - minZ };
        }
        return found ? result : null;
    }
}
