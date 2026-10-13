namespace RimGovernor.Brain.Policy;

/// <summary>The walls and doors standing on the ground, by cell: the geometry a PlannedRoom's ring is matched against.</summary>
public sealed class GroundCensus
{
    public HashSet<Cell> Walls { get; set; } = [];

    public HashSet<Cell> Doors { get; set; } = [];

    /// <summary>The ring of an outdoor room (PlannedRoom.Outdoor): fences.</summary>
    public HashSet<Cell> Fences { get; set; } = [];

    /// <summary>The ring of an outdoor room (PlannedRoom.Outdoor): gates.</summary>
    public HashSet<Cell> Gates { get; set; } = [];

    /// <summary>The animal flaps standing, kept apart from doors: a barn's wall takes one where a room's ring takes a door.</summary>
    public HashSet<Cell> Flaps { get; set; } = [];

    /// <summary>The stuff of each standing wall, where the census names one.</summary>
    public Dictionary<Cell, string> Stuff { get; set; } = [];

    /// <summary>The cells a vent stands in; a vent stands in a wall, so each is also a wall (#2962).</summary>
    public HashSet<Cell> Vents { get; set; } = [];

    /// <summary>The natural rock cells counted as wall (GroundWithRock): nothing builds over them.</summary>
    public HashSet<Cell> Rock { get; set; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>Reads the walls and doors out of the colony's built buildings.</summary>
    public static GroundCensus GroundOf(IReadOnlyList<CurrentBuilding> buildings) => GroundOfWithFlap(buildings, string.Empty);

    /// <summary>GroundOf with the animal flap's def (the catalog's, RoomFurniture.AnimalFlap) read as a flap, never as a door; "" reads none.</summary>
    public static GroundCensus GroundOfWithFlap(IReadOnlyList<CurrentBuilding> buildings, string flap)
    {
        var g = new GroundCensus();
        foreach (var b in buildings)
        {
            var def = b.Building!.Definition;
            foreach (var c in b.Cells)
            {
                if (def == ClimateDefs().VentDef)
                {
                    g.Vents.Add(c);
                    g.Walls.Add(c);
                }
                else if (flap != string.Empty && def == flap)
                {
                    g.Flaps.Add(c);
                }
                else if (def == PenFenceDefinition)
                {
                    g.Fences.Add(c);
                }
                else if (def == PenGateDefinition)
                {
                    g.Gates.Add(c);
                }
                else if (def == ShellWallDefinition)
                {
                    g.Walls.Add(c);
                    g.Stuff[c] = b.Building.Stuff;
                }
                else if (def.Contains("door", StringComparison.OrdinalIgnoreCase))
                {
                    g.Doors.Add(c);
                }
            }
        }
        return g;
    }

    /// <summary>The defs r's ring is raised from: a wall and a door, or a fence and a gate round an outdoor room.</summary>
    public static (string Wall, string Door) RingDefs(this PlannedRoom r) =>
        r.Outdoor ? (PenFenceDefinition, PenGateDefinition) : (ShellWallDefinition, ShellDoorDefinition);

    /// <summary>A copy of set that can be written.</summary>
    internal static HashSet<Cell> WithCells(IEnumerable<Cell> set) => [.. set];

    /// <summary>
    /// The standing walls and doors r's ring is matched against: an outdoor room's fences and gates, the wall
    /// (or animal flap) of the barn it shares a side with standing as fence; a barn's flap cells count as a
    /// door where a flap stands.
    /// </summary>
    internal static (IReadOnlySet<Cell> Walls, IReadOnlySet<Cell> Doors) Ring(this LayoutPlan p, PlannedRoom r, GroundCensus g)
    {
        if (r.Outdoor)
        {
            var fenced = WithCells(g.Fences);
            fenced.UnionWith(g.Walls);
            fenced.UnionWith(g.Flaps);
            return (fenced, g.Gates);
        }
        IReadOnlySet<Cell> doors = g.Doors;
        var flaps = p.FlapCells(r);
        if (flaps.Count > 0)
        {
            var withFlaps = WithCells(doors);
            foreach (var c in flaps)
            {
                if (g.Flaps.Contains(c))
                {
                    withFlaps.Add(c);
                }
                else
                {
                    withFlaps.Remove(c);
                }
            }
            doors = withFlaps;
        }
        return (g.Walls, doors);
    }

    /// <summary>
    /// Reports r's wall ring and doors as standing as planned: a wall on every ring cell, a door on each cell
    /// the plan holds one (ShellDoors) and none elsewhere on the ring. Geometry only; floors and furniture are
    /// separate diffs. No census room need be enclosed yet (a ring still unroofed matches).
    /// </summary>
    public static bool GroundMatches(this LayoutPlan p, PlannedRoom r, GroundCensus g)
    {
        if (r.Unfenced())
        {
            return true; // no ring owed
        }
        var doors = new HashSet<Cell>(p.ShellDoors(r));
        var (walls, standing) = p.Ring(r, g);
        foreach (var c in r.WallRing().Cells())
        {
            if (doors.Contains(c) != standing.Contains(c) || (!doors.Contains(c) && !walls.Contains(c)))
            {
                return false;
            }
        }
        foreach (var a in p.ChildrenOfRole(r.Id, PlannedRole.Airlock))
        {
            if (!standing.Contains(a.Door))
            {
                return false;
            }
            foreach (var c in a.AirlockWalls())
            {
                if (standing.Contains(c) || !walls.Contains(c))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The cells r's shell stands on: the region's wall ring (Region.Walls). A sparring ring and the unfenced
    /// roles with no walls of their own keep their rectangle behaviour: the border of the block they reserve
    /// (ringBlock, rangeBlock, the interior).
    /// </summary>
    internal static Region WallRing(this PlannedRoom r) =>
        r.Unfenced() ? RectRegion(RoomWalls(r)).Perimeter() : r.Region.Walls();

    /// <summary>
    /// The region cell nearest its bounding-box centre (ties to the first in cell order): the centre of a
    /// rectangle, a cell inside an L or a holed region whose centre is not itself part of it.
    /// </summary>
    internal static Cell Anchor(this Region g)
    {
        var b = g.Bounds();
        long cx = (2L * b.X) + b.Width, cz = (2L * b.Z) + b.Height; // centre doubled
        var centre = new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
        if (g.Contains(centre))
        {
            return centre;
        }
        var best = default(Cell);
        var bestD = -1L;
        foreach (var c in g.Cells())
        {
            long dx = (2L * c.X) + 1 - cx, dz = (2L * c.Z) + 1 - cz;
            var d = (dx * dx) + (dz * dz);
            if (bestD < 0 || d < bestD)
            {
                best = c;
                bestD = d;
            }
        }
        return best;
    }

    /// <summary>
    /// The census room standing enclosed on the planned room's anchor cell and lying wholly inside its region:
    /// the lookup for the room's role, quality and temperature. A census room that spills a cell outside the
    /// region (one cell off, or merged with a neighbour) is not the planned room. Whether the room is built is
    /// GroundMatches.
    /// </summary>
    public static (Room? Room, bool Ok) CensusRoomIn(PlannedRoom r, RoomObservation rooms)
    {
        if (r.Region.Empty())
        {
            return (null, false);
        }
        var anchor = r.Region.Anchor();
        foreach (var room in rooms.Rooms)
        {
            if (room.Cells.Count > r.Region.Area())
            {
                continue;
            }
            if (!room.Enclosed.TryGetValue(out var enclosed) || !enclosed)
            {
                continue;
            }
            var hit = false;
            var within = true;
            foreach (var c in room.Cells)
            {
                hit = hit || c == anchor;
                within = within && r.Region.Contains(c);
            }
            if (hit && within)
            {
                return (room, true);
            }
        }
        return (null, false);
    }

    /// <summary>The room's shell: its region walled round, the door where the plan put it. Any region the footprint rules accept (4-connected, at most 3844 cells, a ring of at most 255) shells, rectangular or not.</summary>
    public static RoomFootprint Footprint(this PlannedRoom r) => new(r.Region.Cells(), r.Door, r.DoorRot ?? default);

    /// <summary>
    /// The cells of r's ring that take a door rather than a wall: its own doors, its Link, and any other room's
    /// Link that lies in r's ring (the kitchen's side of the freezer door; the barn's side of the vet room's,
    /// which is no AllRooms room).
    /// </summary>
    public static IReadOnlyList<Cell> ShellDoors(this LayoutPlan p, PlannedRoom r)
    {
        if (r.Unfenced())
        {
            return [];
        }
        var doors = new List<Cell>(p.DoorsOf(r));
        foreach (var a in p.ChildrenOfRole(r.Id, PlannedRole.Airlock))
        {
            doors.Add(a.Door);
        }
        doors.AddRange(p.FlapCells(r));
        var ring = r.Region.Walls();
        foreach (var o in p.RoomsWithHerd())
        {
            if (o.Region.Runs.SequenceEqual(r.Region.Runs))
            {
                continue;
            }
            foreach (var l in p.LinksOf(o))
            {
                if (ring.Contains(l) && r.Region.Adjacent(l) && !doors.Contains(l))
                {
                    doors.Add(l);
                }
            }
        }
        return doors;
    }
}
