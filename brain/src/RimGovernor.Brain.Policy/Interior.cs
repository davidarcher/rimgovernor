namespace RimGovernor.Brain.Policy;

/// <summary>
/// The input to a plan: the role whose template applies, the floor (walls excluded) and the doors in the
/// walls around it.
/// </summary>
public sealed record InteriorRoom
{
    public PlanRole Role { get; init; }

    public Rectangle Interior { get; init; }

    /// <summary>
    /// The floor when it is not the whole Interior rectangle (an L, a notch); null is the rectangle
    /// (Floor). Only the region placer reads it, Interior is its bounding rectangle.
    /// </summary>
    public Region? Region { get; init; }

    public IReadOnlyList<Cell> Doors { get; init; } = [];

    /// <summary>
    /// The doors among Doors that open onto another room; the frame faces a door onto a hallway or
    /// outdoors when it has one.
    /// </summary>
    public IReadOnlyList<Cell> InnerDoors { get; init; } = [];

    /// <summary>
    /// The definitions of the player buildings standing on the floor, sorted; a bench row's pitch widens
    /// to the widest standing member of its family.
    /// </summary>
    public IReadOnlyList<string> Standing { get; init; } = [];

    /// <summary>The furniture the dining and rec templates place, from the catalog rows; the default for a room those templates never plan.</summary>
    public DiningFurniture Dining { get; init; } = new();

    /// <summary>The plannable shapes of the catalog's buildable definitions; a plan needs them to size and sort every piece.</summary>
    public PieceShapes Shapes { get; init; } = new();

    /// <summary>
    /// The pieces the throne template plans besides the throne (the title's braziers, columns and
    /// instrument), each with the definition and shape the catalog gave it.
    /// </summary>
    public IReadOnlyList<RequiredPiece> Required { get; init; } = [];

    /// <summary>
    /// The pieces standing on the floor, as world footprints. The region placer leaves them where they
    /// stand: they block their cells, count toward a want of their definition and link to what is placed
    /// around them, and the plan holds only the pieces still to stand. The templates ignore them.
    /// </summary>
    public IReadOnlyList<InteriorPiece> Built { get; init; } = [];

    /// <summary>
    /// Maps a bench definition to the work chair priced for it (PlannedRoom.WorkChairs); the region placer
    /// stands one on each such bench's interaction cell. Absent leaves the bench without a chair.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkChairs { get; init; } = new Dictionary<string, string>();

    /// <summary>The shape of def for a plan in this room; false when the catalog has no buildable row for it.</summary>
    public (InteriorPieceDef Def, bool Ok) Piece(string def) => Shapes.Get(def);
}

/// <summary>One planned building; Rect is its world footprint.</summary>
public sealed record InteriorPiece
{
    /// <summary>Names the piece within its room ("bed", "bench.2").</summary>
    public string Slot { get; init; } = string.Empty;

    public string Def { get; init; } = string.Empty;

    /// <summary>The definition's native size at rotation North (x, z).</summary>
    public Cell Size { get; init; }

    public Rotation Rot { get; init; }

    public Rectangle Rect { get; init; }

    /// <summary>The definition's interaction cell offset at rotation North, from the anchor; None for pieces without one.</summary>
    public InteractionCell InteractionOffset { get; init; } = new InteractionCell.None();

    /// <summary>Names the want the piece was placed for (RegionWant.Slot).</summary>
    public string Row { get; init; } = string.Empty;

    /// <summary>
    /// Names the Row of the pieces this one must face and touch (a chair at its table); Clear is the
    /// length of the straight lane kept open in front of the piece, where its user stands
    /// (RegionWant.Beside, .Clear).
    /// </summary>
    public string Beside { get; init; } = string.Empty;

    public int Clear { get; init; }

    /// <summary>The cell a native building order names for this footprint.</summary>
    public Cell Anchor() => InteriorRules.AnchorForRect(Rect, Size, Rot);

    /// <summary>The cell the worker stands on, if the piece has one.</summary>
    public (Cell Cell, bool Ok) Interaction()
    {
        if (InteractionOffset is not InteractionCell.At { Offset: var offset })
        {
            return (default, false);
        }
        var a = Anchor();
        var o = InteriorRules.RotateOffset(offset, Rot);
        return (new Cell(a.X + o.X, a.Z + o.Z), true);
    }
}

/// <summary>A room's derived layout. Pieces are the planned footprints, in world cells.</summary>
public sealed record InteriorPlan(IReadOnlyList<InteriorPiece> Pieces)
{
    public InteriorPlan() : this([])
    {
    }
}

public static class InteriorRules
{
    /// <summary>
    /// The room's floor seen from its entrance: Width cells along the entrance wall, Depth away from it,
    /// the entrance in column Entrance, on the left half of the wall. The entrance is the first door in
    /// cell order that opens onto a hallway or outdoors, or the first door when every door leads into
    /// another room. False when the room has no door in its walls.
    /// </summary>
    internal static (RegionFrame Frame, bool Ok) EntranceFrame(InteriorRoom room)
    {
        var r = room.Interior;
        if (r.Width <= 0 || r.Height <= 0 || room.Doors.Count == 0)
        {
            return (default, false);
        }
        var inner = new HashSet<Cell>(room.InnerDoors);
        var doors = room.Doors.SortedBy((a, b) =>
        {
            if (inner.Contains(a) != inner.Contains(b))
            {
                return !inner.Contains(a);
            }
            return CombatManhunterRules.CellLess(a, b);
        });
        var (side, ok) = DoorSide(r, doors[0]);
        if (!ok)
        {
            return (default, false);
        }
        var d = doors[0];
        var (width, depth, u) = side == Rotation.South ? (r.Width, r.Height, d.X - r.X)
            : side == Rotation.North ? (r.Width, r.Height, r.X + r.Width - 1 - d.X)
            : side == Rotation.West ? (r.Height, r.Width, r.Z + r.Height - 1 - d.Z)
            : (r.Height, r.Width, d.Z - r.Z);
        return (new RegionFrame(width, depth, Math.Min(u, width - 1 - u)), true);
    }

    /// <summary>
    /// Reads a census room as a plan input under the given role: only a room whose cells fill their
    /// bounding rectangle has an interior, and its doors are the doorways beside that rectangle's sides.
    /// </summary>
    public static (InteriorRoom? Room, bool Ok) InteriorRoomFromCensus(IReadOnlyList<Cell> cells, PlanRole role, IReadOnlyList<Cell> doorways, PieceShapes shapes)
    {
        if (cells.Count == 0)
        {
            return (null, false);
        }
        var rect = PlacementSearchRules.CellsRectangle(cells);
        var seen = new HashSet<Cell>(cells);
        if (seen.Count != (long)rect.Width * rect.Height)
        {
            return (null, false);
        }
        var doors = new List<Cell>();
        foreach (var d in doorways)
        {
            if (DoorSide(rect, d).Ok)
            {
                doors.Add(d);
            }
        }
        var sorted = doors.SortedBy(CombatManhunterRules.CellLess);
        return (new InteriorRoom { Role = role, Interior = rect, Shapes = shapes, Doors = sorted }, sorted.Count > 0);
    }

    /// <summary>Maps a layout module to the census role its furniture gives it.</summary>
    internal static readonly IReadOnlyDictionary<PlannedRole, PlanRole> ModuleRoomRoles = new Dictionary<PlannedRole, PlanRole>
    {
        [PlannedRole.Bedroom] = RoomRole.Bedroom,
        [PlannedRole.Barracks] = RoomRole.Barracks,
        [PlannedRole.Suite] = PlanRole.Suite,
        [PlannedRole.Dining] = RoomRole.DiningRoom,
        [PlannedRole.Rec] = RoomRole.RecRoom,
        [PlannedRole.Lab] = RoomRole.Laboratory,
        [PlannedRole.Tomb] = RoomRole.Tomb,
        [PlannedRole.Throne] = RoomRole.ThroneRoom,
        [PlannedRole.Nursery] = RoomRole.Nursery,
        [PlannedRole.Classroom] = RoomRole.Classroom,
        [PlannedRole.Worship] = RoomRole.WorshipRoom,
        [PlannedRole.DeathrestChamber] = RoomRole.DeathrestChamber,
        [PlannedRole.ContainmentCell] = RoomRole.ContainmentCell,
        [PlannedRole.IsolationRoom] = PlanRole.IsolationRoom,
        [PlannedRole.Hospital] = RoomRole.Hospital,
        [PlannedRole.Kitchen] = RoomRole.Kitchen,
        [PlannedRole.Workshop] = RoomRole.Workshop,
        [PlannedRole.Storage] = RoomRole.Storeroom,
        [PlannedRole.Prison] = RoomRole.PrisonCell,
    };

    /// <summary>Reads a layout room as a plan input.</summary>
    public static (InteriorRoom? Room, bool Ok) InteriorRoomFromLayout(PlannedRoom r, PieceShapes shapes)
    {
        var bounds = r.Region.Bounds();
        if (!ModuleRoomRoles.TryGetValue(r.Role, out var role) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (null, false);
        }
        if (!DoorSide(bounds, r.Door).Ok)
        {
            return (null, false);
        }
        return (new InteriorRoom
        {
            Role = role,
            Interior = bounds,
            Doors = [r.Door],
            Shapes = shapes,
            WorkChairs = r.WorkChairs,
            Region = r.Region.IsRect() ? null : r.Region,
        }, true);
    }

    /// <summary>The direction from the room to a door in one of its walls (corners excluded).</summary>
    internal static (Rotation Side, bool Ok) DoorSide(Rectangle r, Cell d)
    {
        var inX = d.X >= r.X && d.X < r.X + r.Width;
        var inZ = d.Z >= r.Z && d.Z < r.Z + r.Height;
        if (inX && d.Z == r.Z - 1)
        {
            return (Rotation.South, true);
        }
        if (inX && d.Z == r.Z + r.Height)
        {
            return (Rotation.North, true);
        }
        if (inZ && d.X == r.X - 1)
        {
            return (Rotation.West, true);
        }
        if (inZ && d.X == r.X + r.Width)
        {
            return (Rotation.East, true);
        }
        return (default, false);
    }

    internal static readonly Rotation[] RotationOrder = [Rotation.North, Rotation.East, Rotation.South, Rotation.West];

    internal static (int Index, bool Ok) RotationIndex(Rotation r)
    {
        for (var i = 0; i < RotationOrder.Length; i++)
        {
            if (RotationOrder[i] == r)
            {
                return (i, true);
            }
        }
        return (0, false);
    }

    /// <summary>Turns r clockwise by steps quarter turns.</summary>
    internal static Rotation RotateCW(Rotation r, int steps)
    {
        var (i, _) = RotationIndex(r);
        return RotationOrder[(((i + steps) % 4) + 4) % 4];
    }

    /// <summary>A definition's footprint dimensions at a rotation.</summary>
    internal static (int Width, int Height, bool Ok) RotatedSize(Cell size, Rotation rot)
    {
        if (size.X < 1 || size.Z < 1)
        {
            return (0, 0, false);
        }
        return rot is Rotation.North or Rotation.South ? (size.X, size.Z, true) : (size.Z, size.X, true);
    }

    /// <summary>RimWorld's GenAdj.OccupiedRect: the footprint a building of the given North size occupies when anchored at a cell.</summary>
    public static Rectangle OccupiedRect(Cell anchor, Cell size, Rotation rot)
    {
        var (w, h, ok) = RotatedSize(size, rot);
        if (!ok)
        {
            return default;
        }
        int cx = anchor.X, cz = anchor.Z;
        if (!(size.X == 1 && size.Z == 1))
        {
            switch (rot)
            {
                case Rotation.East:
                    if (h % 2 == 0)
                    {
                        cz--;
                    }
                    break;
                case Rotation.South:
                    if (w % 2 == 0)
                    {
                        cx--;
                    }
                    if (h % 2 == 0)
                    {
                        cz--;
                    }
                    break;
                case Rotation.West:
                    if (w % 2 == 0)
                    {
                        cx--;
                    }
                    break;
            }
        }
        return new Rectangle(cx - ((w - 1) / 2), cz - ((h - 1) / 2), w, h);
    }

    /// <summary>Inverts OccupiedRect.</summary>
    public static Cell AnchorForRect(Rectangle r, Cell size, Rotation rot)
    {
        var probe = OccupiedRect(default, size, rot);
        return new Cell(r.X - probe.X, r.Z - probe.Z);
    }

    /// <summary>Turns a North offset to a rotation (RimWorld's IntVec3.RotatedBy).</summary>
    internal static Cell RotateOffset(Cell o, Rotation rot) =>
        rot == Rotation.North ? o
        : rot == Rotation.East ? new Cell(o.Z, -o.X)
        : rot == Rotation.South ? new Cell(-o.X, -o.Z)
        : new Cell(-o.Z, o.X);

    /// <summary>
    /// The cells the fallback search tries before any other free cell of the room: the wall bands, the
    /// centre lines and the mirror images of occupied cells across both centre lines.
    /// </summary>
    public static IReadOnlyList<Cell> InteriorSnapAnchors(InteriorRoom room, IReadOnlyList<Cell> occupied)
    {
        var r = room.Interior;
        bool Inside(Cell c) => c.X >= r.X && c.Z >= r.Z && c.X < r.X + r.Width && c.Z < r.Z + r.Height;
        var snap = new Dictionary<Cell, bool>();
        foreach (var c in PlannedRules.RectCells(r))
        {
            int u = c.X - r.X, v = c.Z - r.Z;
            var band = u == 0 || v == 0 || u == r.Width - 1 || v == r.Height - 1;
            var centre = (2 * u) + 1 == r.Width || (2 * u) + 2 == r.Width || (2 * v) + 1 == r.Height || (2 * v) + 2 == r.Height;
            snap[c] = band || centre;
        }
        foreach (var c in occupied)
        {
            if (!Inside(c))
            {
                continue;
            }
            snap[new Cell((2 * r.X) + r.Width - 1 - c.X, c.Z)] = true;
            snap[new Cell(c.X, (2 * r.Z) + r.Height - 1 - c.Z)] = true;
        }
        return snap.Where(kv => kv.Value).Select(kv => kv.Key).SortedBy(CombatManhunterRules.CellLess);
    }

    /// <summary>
    /// The layout module a census room role is planned as, the inverse of ModuleRoomRoles; Storeroom is
    /// the plain storage module. False means no planned room carries the role.
    /// </summary>
    public static (PlannedRole Role, bool Ok) PlannedRoleOf(RoomRole role)
    {
        if (role == RoomRole.Storeroom)
        {
            return (PlannedRole.Storage, true);
        }
        foreach (var (m, r) in ModuleRoomRoles)
        {
            if (r == role)
            {
                return (m, true);
            }
        }
        return (default, false);
    }
}
