namespace RimGovernor.Brain.Policy;

// The child rooms' layout: nursery, playroom and classroom are core rooms sized from the footprints they hold
// (ChildRoomShape), sited at the nearest core slot like the throne room. Furniture stands in bands of free
// floor: a band as high as the piece, a free row between bands, the row inside the entrance free, and the
// entrance's column free the full depth so every band stays reachable.
public static partial class PolicyConstants
{
    /// <summary>Why the graph holds a child room: a pawn of the matching developmental stage lives in the colony, or the plan already holds the room (#3003).</summary>
    public const string ReasonChildRoom = "child_room_owed";

    /// <summary>
    /// The add-on rooms the graph holds as room nodes, all those of ChildRoomNeeds but the nursery, which is a
    /// station group (the cribs of the babies); the rest have no station.
    /// </summary>
    internal static readonly IReadOnlyList<PlannedRole> ChildNodeRoles =
        [PlannedRole.Playroom, PlannedRole.Classroom, PlannedRole.DeathrestChamber, PlannedRole.Worship, PlannedRole.ContainmentCell, PlannedRole.IsolationRoom];
}

public static partial class PolicyFunctions
{
    /// <summary>The shapes of every need whose furniture resolves, planned or not.</summary>
    public static IReadOnlyList<ChildRoomShape> ChildRoomShapes(IReadOnlyList<ChildRoomNeed> needs, IReadOnlyList<FurnitureDefinition> defs)
    {
        var output = new List<ChildRoomShape>();
        foreach (var n in needs)
        {
            var (s, ok) = n.Shape(defs);
            if (ok)
            {
                output.Add(s!);
            }
        }
        return output;
    }

    /// <summary>The canonical corners a piece of size stands at in a width by depth room whose entrance column is aisle, back band first.</summary>
    internal static IReadOnlyList<Cell> ChildSlots(int width, int depth, int aisle, Cell size)
    {
        var output = new List<Cell>();
        if (size.X < 1 || size.Z < 1)
        {
            return output;
        }
        for (var z = depth - size.Z; z >= 1; z -= size.Z + 1)
        {
            for (var x = 0; x + size.X <= width;)
            {
                if (x <= aisle && aisle < x + size.X)
                {
                    x = aisle + 1;
                    continue;
                }
                output.Add(new Cell(x, z));
                x += size.X;
            }
        }
        return output;
    }

    /// <summary>How many pieces of size a width by depth room holds wherever its entrance stands.</summary>
    internal static int ChildCapacity(int width, int depth, Cell size)
    {
        var capacity = -1;
        for (var aisle = 0; aisle < width; aisle++)
        {
            var n = ChildSlots(width, depth, aisle, size).Count;
            if (capacity < 0 || n < capacity)
            {
                capacity = n;
            }
        }
        return Math.Max(capacity, 0);
    }

    /// <summary>Reports whether a width by depth interior holds every piece.</summary>
    internal static bool Holds(this ChildRoomShape s, int width, int depth) =>
        s.Pieces.All(p => ChildCapacity(width, depth, p.Size) >= p.Count);

    /// <summary>
    /// The interiors (width along the spine, depth away from it) that hold the shape, best first: the fewest
    /// cells, then the aspect nearest two thirds. Depth stays within the core's cross-section.
    /// </summary>
    public static IReadOnlyList<(int W, int D)> ChildRoomSizes(ChildRoomShape s)
    {
        var output = new List<(int W, int D)>();
        for (var d = ThroneMinSide; d <= CoreMaxDepth; d++)
        {
            for (var w = ThroneMinSide; ; w++)
            {
                if (s.Holds(w, d))
                {
                    output.Add((w, d));
                    break;
                }

                // Width past the whole demand in one row cannot help.
                if (w > ChildRowDemand(s) + ThroneMinSide)
                {
                    break;
                }
            }
        }
        return output.SortedBy((a, b) =>
        {
            int ca = a.W * a.D, cb = b.W * b.D;
            if (ca != cb)
            {
                return ca < cb;
            }
            return Math.Abs((2 * a.W) - (3 * a.D)) < Math.Abs((2 * b.W) - (3 * b.D));
        });
    }

    /// <summary>
    /// The playroom, classroom, deathrest chamber, worship room, containment cell and isolation room the colony
    /// owes (shapes, ChildRoomShapes) or the plan holds as room nodes. An owed room is the smallest interior
    /// that holds its furniture (ChildRoomSizes); a standing one keeps at least the floor it has, so it is a
    /// node to pin whether or not its need has ended. The short side is fixed, the long side follows the area.
    /// </summary>
    public static IReadOnlyList<RoomNode> ChildRoomNodes(LayoutPlan plan, IReadOnlyList<ChildRoomShape> shapes)
    {
        var nodes = new List<RoomNode>();
        foreach (var role in ChildNodeRoles)
        {
            int w = 0, d = 0;
            foreach (var s in shapes)
            {
                var sizes = ChildRoomSizes(s);
                if (s.Module == role && sizes.Count > 0)
                {
                    (w, d) = sizes[0];
                    break;
                }
            }
            foreach (var r in plan.RoomsOf(role))
            {
                var b = r.Region.Bounds();
                if (b.Width * b.Height > w * d)
                {
                    (w, d) = (b.Width, b.Height);
                }
            }
            if (w > 0 && d > 0)
            {
                nodes.Add(new RoomNode { Id = Wire.Name(role), Role = role, Area = w * d, Width = Math.Min(w, d), Reason = ReasonChildRoom });
            }
        }
        return nodes;
    }

    /// <summary>The width one row would need for every piece.</summary>
    private static int ChildRowDemand(ChildRoomShape s) => s.Pieces.Sum(p => p.Size.X * p.Count);

    /// <summary>The plan's first room of the shape's role that holds it.</summary>
    public static (PlannedRoom? Room, bool Ok) ChildRoomFor(this LayoutPlan p, ChildRoomShape s)
    {
        foreach (var r in p.Rooms)
        {
            var (w, d) = FrameDims(r);
            if (r.Role == s.Module && s.Holds(w, d))
            {
                return (r, true);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// The room's interior as its furniture frame sees it: width along the door's wall, depth away from it. A
    /// room beside a north-south hallway is stored transposed, so its door faces east or west and the frame's
    /// width is the interior's height.
    /// </summary>
    internal static (int Width, int Depth) FrameDims(PlannedRoom r)
    {
        var b = r.Region.Bounds();
        var (side, ok) = DoorSide(b, r.Door);
        if (ok && side is Rotation.East or Rotation.West)
        {
            return (b.Height, b.Width);
        }
        return (b.Width, b.Height);
    }
}
