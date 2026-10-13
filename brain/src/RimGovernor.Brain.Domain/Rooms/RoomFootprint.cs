namespace RimGovernor.Brain.Domain;

/// <summary>One boundary door of a footprint: its wall cell and the side it faces out of the room.</summary>
public readonly record struct RoomDoor(Cell Cell, Rotation Entrance)
{
    /// <summary>The cell straight outside the door.</summary>
    public Cell Threshold
    {
        get
        {
            var step = Entrance.Outward();
            return new Cell(Cell.X + step.X, Cell.Z + step.Z);
        }
    }
}

/// <summary>
/// The definitions and stuff a shell is expanded with: the wall and door definitions and the door and wall stuff.
/// </summary>
public readonly record struct ShellStyle(string WallDef, string DoorDef, string DoorStuff, string WallStuff);

/// <summary>
/// One room rectangle: a nonnegative anchor and a 4..64 extent on each axis. The lower bound of four is what
/// gives a shell an interior at all.
/// </summary>
public readonly record struct RoomBounds(int X, int Z, int Width, int Height)
{
    /// <summary>Throws <see cref="DomainException"/> when the anchor or extent is out of range.</summary>
    public void Validate()
    {
        DomainException.Require(X >= 0 && Z >= 0, "room anchor must be nonnegative");
        DomainException.Require(
            Width >= 4 && Width <= 64 && Height >= 4 && Height <= 64,
            "a room shell needs an interior; use a building batch for a wall segment"
        );
    }
}

/// <summary>
/// One exact-cell room: an orthogonally connected interior, the wall ring that encloses it and a single
/// boundary door (plus optional extra doors). It is the geometry every room shape shares, whether it came from a
/// rectangle, an ellipse template or an irregular footprint grown over constrained terrain.
/// The wall ring is the interior's 8-neighbourhood boundary: every cell that touches the interior orthogonally or
/// diagonally and is not interior itself, so a rectangle, a concave template and a grown irregular room all
/// expand by one rule. The largest interior is 3844 cells, the same bound RoomAdoption enforces.
/// Nothing here establishes that the cells are buildable; native footprint, terrain, reachability, cost and
/// placement legality are established at inspection and dispatch for each expanded Building.
/// </summary>
public sealed record RoomFootprint
{
    /// <summary>
    /// Go <c>NewRoomFootprint</c>: validates one exact interior and its entrance. The interior must be nonempty,
    /// duplicate free, clear of the map edge by one cell for its walls and orthogonally connected. Each door must
    /// be a wall cell whose inward neighbour (against the entrance side) is interior and whose outward neighbour
    /// is neither interior nor wall, and doors must be distinct. The wall ring must fit a committed plan: at most
    /// 255 placements. Throws <see cref="DomainException"/> when invalid.
    /// </summary>
    public RoomFootprint(IEnumerable<Cell> interior, Cell door, Rotation entrance, params RoomDoor[] extra)
    {
        DomainException.Require(Wire.IsDefined(entrance), "invalid room entrance side");
        var cells = interior.ToList();
        DomainException.Require(cells.Count != 0 && cells.Count <= 3844, "room interior cell count out of range");
        var inside = new HashSet<Cell>(cells.Count);
        foreach (var cell in cells)
        {
            DomainException.Require(cell.X >= 1 && cell.Z >= 1, "room interior must leave space for its walls");
            DomainException.Require(inside.Add(cell), "duplicate room interior cell");
        }
        DomainException.Require(ConnectedCells(cells[0], inside), "room interior must be orthogonally connected");
        var wall = new HashSet<Cell>();
        foreach (var cell in inside)
        {
            foreach (var next in Neighbours8(cell))
            {
                if (!inside.Contains(next))
                {
                    wall.Add(next);
                }
            }
        }
        var seen = new HashSet<Cell>();
        foreach (var d in extra.Prepend(new RoomDoor(door, entrance)))
        {
            DomainException.Require(Wire.IsDefined(d.Entrance), "invalid room entrance side");
            var step = d.Entrance.Outward();
            var inward = new Cell(d.Cell.X - step.X, d.Cell.Z - step.Z);
            var outward = new Cell(d.Cell.X + step.X, d.Cell.Z + step.Z);
            DomainException.Require(
                wall.Contains(d.Cell) && inside.Contains(inward) && !inside.Contains(outward) && !wall.Contains(outward) && seen.Add(d.Cell),
                "room door must be a wall cell facing outside"
            );
        }
        DomainException.Require(wall.Count <= 255, "room wall ring exceeds the committed plan bound");
        Walls = EquatableArray.From(wall.OrderBy(c => c.Z).ThenBy(c => c.X));
        Interior = EquatableArray.From(inside.OrderBy(c => c.Z).ThenBy(c => c.X));
        Door = door;
        Entrance = entrance;
        ExtraDoors = EquatableArray.From(extra);
    }

    /// <summary>The interior cells in z-outer, x-inner order.</summary>
    public EquatableArray<Cell> Interior { get; }

    /// <summary>The wall ring in z-outer, x-inner order.</summary>
    public EquatableArray<Cell> Walls { get; }

    public Cell Door { get; }

    public Rotation Entrance { get; }

    /// <summary>The doors beyond the primary one, empty for a single-door room.</summary>
    public EquatableArray<RoomDoor> ExtraDoors { get; }

    /// <summary>Every door, the primary first.</summary>
    public IReadOnlyList<RoomDoor> Doors => [new RoomDoor(Door, Entrance), .. ExtraDoors];

    /// <summary>The cell straight outside the primary door, the ground the room is entered from.</summary>
    public Cell Threshold => new RoomDoor(Door, Entrance).Threshold;

    /// <summary>Every cell the shell occupies, interior and wall ring together, for site legality checks.</summary>
    public IReadOnlyList<Cell> Cells => [.. Interior, .. Walls];

    /// <summary>The smallest rectangle covering interior and walls.</summary>
    public RoomBounds Bounds
    {
        get
        {
            var minX = Walls[0].X;
            var minZ = Walls[0].Z;
            var maxX = minX;
            var maxZ = minZ;
            foreach (var c in Walls)
            {
                minX = Math.Min(minX, c.X);
                maxX = Math.Max(maxX, c.X);
                minZ = Math.Min(minZ, c.Z);
                maxZ = Math.Max(maxZ, c.Z);
            }
            return new RoomBounds(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
        }
    }

    /// <summary>
    /// Whether every interior cell lies within <paramref name="maxDistance"/> (the game's
    /// RoofCollapseUtility.RoofMaxSupportDistance, the catalog's roof_max_support_distance) of some wall cell, so
    /// the game's automatic roofing covers the completed shell without columns.
    /// </summary>
    public bool RoofSupported(double maxDistance)
    {
        foreach (var cell in Interior)
        {
            var supported = false;
            foreach (var w in Walls)
            {
                long dx = (long)cell.X - w.X;
                long dz = (long)cell.Z - w.Z;
                if ((double)(dx * dx + dz * dz) <= maxDistance * maxDistance)
                {
                    supported = true;
                    break;
                }
            }
            if (!supported)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Go <c>SameRoomFootprint</c>: whether two footprints hold identical geometry.</summary>
    public static bool SameRoomFootprint(RoomFootprint a, RoomFootprint b) => a == b;

    /// <summary>
    /// Expands the shell: the door first, carrying the door definition and the entrance rotation, then every other
    /// wall cell carrying the wall definition facing north, in z-outer, x-inner order. Interior cells are
    /// deliberately absent: this is a shell, not a floor.
    /// </summary>
    public IReadOnlyList<Building> Placements(string wallDef, string doorDef, string material) =>
        StyledPlacements(new ShellStyle(wallDef, doorDef, material, material));

    /// <summary>
    /// Placements with the style choosing the stuff: the door first with its own stuff, then every wall cell with
    /// the wall stuff. Expands to nothing when a definition is invalid (Go returned nil).
    /// </summary>
    public IReadOnlyList<Building> StyledPlacements(ShellStyle style)
    {
        try
        {
            var doors = new HashSet<Cell> { Door };
            var placements = new List<Building>(Walls.Count) { new Building(style.DoorDef, Door, Entrance, style.DoorStuff) };
            foreach (var d in ExtraDoors)
            {
                placements.Add(new Building(style.DoorDef, d.Cell, d.Entrance, style.DoorStuff));
                doors.Add(d.Cell);
            }
            foreach (var cell in Walls)
            {
                if (!doors.Contains(cell))
                {
                    placements.Add(new Building(style.WallDef, cell, Rotation.North, style.WallStuff));
                }
            }
            return placements;
        }
        catch (DomainException)
        {
            return [];
        }
    }

    /// <summary>
    /// Go <c>RectangleFootprint</c>: the rectangular shell RoomShell describes. Bounds cover walls and interior;
    /// the door sits at the midpoint of the entrance side exactly as RoomShell.Door reports.
    /// </summary>
    public static RoomFootprint RectangleFootprint(RoomBounds bounds, Rotation entrance)
    {
        bounds.Validate();
        var interior = new List<Cell>();
        for (var z = bounds.Z + 1; z < bounds.Z + bounds.Height - 1; z++)
        {
            for (var x = bounds.X + 1; x < bounds.X + bounds.Width - 1; x++)
            {
                interior.Add(new Cell(x, z));
            }
        }
        var door = entrance switch
        {
            Rotation.North => new Cell(bounds.X + bounds.Width / 2, bounds.Z + bounds.Height - 1),
            Rotation.South => new Cell(bounds.X + bounds.Width / 2, bounds.Z),
            Rotation.East => new Cell(bounds.X + bounds.Width - 1, bounds.Z + bounds.Height / 2),
            Rotation.West => new Cell(bounds.X, bounds.Z + bounds.Height / 2),
            _ => throw new DomainException("invalid room entrance side"),
        };
        return new RoomFootprint(interior, door, entrance);
    }

    private static Cell[] Neighbours8(Cell c) =>
        [
            new(c.X + 1, c.Z),
            new(c.X + 1, c.Z + 1),
            new(c.X, c.Z + 1),
            new(c.X - 1, c.Z + 1),
            new(c.X - 1, c.Z),
            new(c.X - 1, c.Z - 1),
            new(c.X, c.Z - 1),
            new(c.X + 1, c.Z - 1),
        ];

    // Whether the orthogonally connected region of points reachable from seed is the whole set.
    private static bool ConnectedCells(Cell seed, HashSet<Cell> points)
    {
        if (!points.Contains(seed))
        {
            return false;
        }
        var seen = new HashSet<Cell> { seed };
        var stack = new Stack<Cell>();
        stack.Push(seed);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            foreach (var next in new Cell[] { new(at.X + 1, at.Z), new(at.X - 1, at.Z), new(at.X, at.Z + 1), new(at.X, at.Z - 1) })
            {
                if (points.Contains(next) && seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
        return seen.Count == points.Count;
    }
}

public static class RotationExtensions
{
    /// <summary>
    /// The unit step from inside the room out through an entrance on this side (north is +Z, south -Z, east +X,
    /// west -X).
    /// </summary>
    internal static Cell Outward(this Rotation rotation) =>
        rotation switch
        {
            Rotation.North => new Cell(0, 1),
            Rotation.South => new Cell(0, -1),
            Rotation.East => new Cell(1, 0),
            _ => new Cell(-1, 0),
        };
}
