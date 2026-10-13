namespace RimGovernor.Brain.Policy;

// Go: clearance_ground.go
// Planned-ground clearance: when a planned room needs ground, every building and constructed floor on that
// ground the plan does not hold is cleared, the colony's own included. Per room the order is furniture and
// other non-wall buildings, then each door standing on the room's wall ring where the plan has none (swapped
// for a wall in place, so the enclosure holds), then the roof and the walls and doors holding it, then the
// floors once the cells are clear.

/// <summary>One constructed floor cell on planned ground.</summary>
public sealed record ClearanceFloor
{
    public Cell Cell { get; init; }

    public string DefName { get; init; } = "";

    public bool Designated { get; init; }
}

/// <summary>The stage a planned room's ground clearance is in.</summary>
public enum GroundPhase
{
    None,

    [WireName("furniture")]
    Furniture,

    /// <summary>Packs the room's packable furniture in one batch.</summary>
    [WireName("pack")]
    Pack,

    [WireName("doors")]
    Doors,

    [WireName("walls")]
    Walls,

    [WireName("floors")]
    Floors,
}

/// <summary>
/// The ground of rooms the plan retired among the ground planned clearance is given, with the wall rectangles
/// of the rooms the plan still holds: a building on a retired room's ring that also lies in a kept room's
/// walls is that room's and stays. Nothing on retired ground is planned: its ring walls, doors and frames
/// come down and its sleeping spots go; a research table is packed with the rest and installed in the
/// laboratory.
/// </summary>
public sealed record RetiredGround
{
    public EquatableArray<Region> Ground { get; init; }

    public EquatableArray<Region> Kept { get; init; }

    internal bool Has(Region g) => Ground.Items.Any(g.Equal);

    internal bool IsKept(Cell c) => Kept.Items.Any(k => k.Contains(c));

    internal bool HeldRow(ClearanceTarget row)
    {
        for (var x = row.Minimum.X; x <= row.Maximum.X; x++)
        {
            for (var z = row.Minimum.Z; z <= row.Maximum.Z; z++)
            {
                if (IsKept(new Cell(x, z)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// One clearance method: the phase labelling it, its targets (buildings or floors), the roof cells to remove
/// (walls) and the ground the walls are deconstructed on. <see cref="Ground"/> is the retired ground it
/// clears, zero for the batch of planned rooms.
/// </summary>
public sealed record GroundStep
{
    public Region Ground { get; init; }

    public GroundPhase Phase { get; init; }

    public EquatableArray<ClearanceTarget> Targets { get; init; }

    public EquatableArray<ClearanceFloor> Floors { get; init; }

    public EquatableArray<Cell> Roof { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The ground of every open-ground planned room whose ring does not yet match the plan (GroundMatches), in
    /// plan order: its interior and its wall ring, standing rooms included; then the retired ground.
    /// </summary>
    public static List<Region> PlannedGround(LayoutPlan plan, GroundCensus ground)
    {
        var output = new List<Region>();
        foreach (var r in plan.GroundRooms(ground))
        {
            output.Add(RectRegion(RoomGround(r.Region.Bounds())));
            foreach (var c in plan.ChildrenOf(r.ID))
            {
                output.Add(RectRegion(c.AttachedGround()));
            }
        }

        output.AddRange(plan.RetiredGround.Items);
        return output;
    }

    /// <summary>A room's interior and wall ring, clipped to the map's origin.</summary>
    private static Rectangle RoomGround(Rectangle room)
    {
        var g = new Rectangle
        {
            X = room.X - 1,
            Z = room.Z - 1,
            Width = room.Width + 2,
            Height = room.Height + 2,
        };
        if (g.X < 0)
        {
            g = g with { Width = g.Width + g.X, X = 0 };
        }

        if (g.Z < 0)
        {
            g = g with { Height = g.Height + g.Z, Z = 0 };
        }

        return g;
    }

    /// <summary>The plan's retired ground with its kept rooms' walls.</summary>
    public static RetiredGround RetiredGroundOf(LayoutPlan plan)
    {
        var kept = new List<Region>();
        foreach (var r in plan.RoomsWithHerd())
        {
            // Only the ring: an old wall that now stands inside a reshaped room is not its wall.
            kept.Add(r.WallRing());
        }

        return new RetiredGround { Ground = plan.RetiredGround, Kept = EquatableArray.From(kept) };
    }

    /// <summary>
    /// The retired ground with nothing left to clear: no building and no floor of ours that is not a kept
    /// room's. The plan drops these entries.
    /// </summary>
    public static List<Region> RetiredGroundDone(LayoutPlan plan, IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<ClearanceFloor> floors)
    {
        var rg = RetiredGroundOf(plan);
        return [.. rg.Ground.Items.Where(g => !RetiredWork(rows, floors, g, rg))];
    }

    /// <summary>The plan less the entries of <paramref name="done"/>.</summary>
    public static LayoutPlan WithoutRetiredGround(this LayoutPlan p, IReadOnlyCollection<Region> done) =>
        p with { RetiredGround = EquatableArray.From(p.RetiredGround.Items.Where(g => !done.Any(g.Equal))) };

    /// <summary>A building or floor left on retired ground <paramref name="g"/>.</summary>
    private static bool RetiredWork(IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<ClearanceFloor> floors, Region g, RetiredGround rg)
    {
        if (rows.Any(row => GroundTarget(row, g, rg)))
        {
            return true;
        }

        return floors.Any(f => g.Contains(f.Cell) && !rg.IsKept(f.Cell) && !GroundCovered(f.Cell, rows));
    }

    /// <summary>A building clearance of retired ground <paramref name="g"/> takes down: everything but a kept room's walls.</summary>
    private static bool GroundTarget(ClearanceTarget row, Region g, RetiredGround rg) => row.Player && Overlaps(row, g) && !rg.HeldRow(row);

    /// <summary>Separates the player rows only planned ground reports from the non-player census home clearance and salvage read.</summary>
    public static (List<ClearanceTarget> Others, List<ClearanceTarget> Player) SplitGroundRows(IEnumerable<ClearanceTarget> rows)
    {
        var others = new List<ClearanceTarget>();
        var player = new List<ClearanceTarget>();
        foreach (var row in rows)
        {
            (row.Player ? player : others).Add(row);
        }

        return (others, player);
    }

    /// <summary>
    /// A shelter sleeping spot: the colony's only bed until the room stands, so planned-ground clearance
    /// leaves it. Clearing it first left the pawns sleeping outside (shelter unmet, the comfort planners held)
    /// while the room was still being dug.
    /// </summary>
    internal static bool StandInBed(ClearanceTarget row) => row.DefName == "SleepingSpot";

    /// <summary>
    /// Picks the first retired ground, in plan order, with work left and its earliest phase: furniture,
    /// packs, then walls with their roof, then floors. Rows are the census's player rows on the ground
    /// (SplitGroundRows). Null when the ground is clear. Planned rooms go through Reconcile
    /// (PlannedGroundStep).
    /// </summary>
    internal static GroundStep? RetiredGroundStep(
        IReadOnlyList<ClearanceTarget> rows,
        IReadOnlyList<ClearanceFloor> floors,
        EquatableArray<Region> ground,
        RoomObservation rooms,
        RetiredGround rg
    )
    {
        var ordered = rows.OrderBy(r => r.EntityID, StringComparer.Ordinal).ToList();
        var cells = floors.OrderBy(f => f.Cell.Z).ThenBy(f => f.Cell.X).ToList();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var claimedFloor = new HashSet<Cell>();
        foreach (var g in ground.Items)
        {
            var furniture = new List<ClearanceTarget>();
            var packs = new List<ClearanceTarget>();
            var walls = new List<ClearanceTarget>();
            foreach (var row in ordered)
            {
                if (claimed.Contains(row.EntityID) || !GroundTarget(row, g, rg))
                {
                    continue;
                }

                claimed.Add(row.EntityID);
                if (row.EnclosesRoom)
                {
                    walls.Add(row);
                }
                else if (row.Packable && !row.Designated)
                {
                    packs.Add(row);
                }
                else
                {
                    furniture.Add(row);
                }
            }

            // Pieces in use go after every other piece in the room.
            packs = [.. packs.OrderBy(p => p.InUse)];
            if (furniture.Count > 0)
            {
                return new GroundStep { Ground = g, Phase = GroundPhase.Furniture, Targets = EquatableArray.From(furniture) };
            }

            if (packs.Count > 0)
            {
                return new GroundStep { Ground = g, Phase = GroundPhase.Pack, Targets = EquatableArray.From(packs) };
            }

            if (walls.Count > 0 && HoldsStandingRoom(walls, ground.Items, rooms))
            {
                // A reshaped room's old wall still bounds the room that stands: it comes down once the new
                // ring has split it off. Floors wait too.
                continue;
            }

            if (walls.Count > 0)
            {
                return new GroundStep
                {
                    Ground = g,
                    Phase = GroundPhase.Walls,
                    Targets = EquatableArray.From(walls),
                    Roof = EquatableArray.From(EnclosedRoof(walls, ground.Items, rooms)),
                };
            }

            var mine = new List<ClearanceFloor>();
            foreach (var f in cells)
            {
                if (g.Contains(f.Cell) && !claimedFloor.Contains(f.Cell) && !GroundCovered(f.Cell, ordered) && !(rg.Has(g) && rg.IsKept(f.Cell)))
                {
                    claimedFloor.Add(f.Cell);
                    mine.Add(f);
                }
            }

            if (mine.Count > 0)
            {
                return new GroundStep { Ground = g, Phase = GroundPhase.Floors, Floors = EquatableArray.From(mine) };
            }
        }

        return null;
    }

    /// <summary>The clearance deficit retired ground owes: every target building and every floor cell no player building covers, stable.</summary>
    internal static List<string> RetiredGroundWork(
        IReadOnlyList<ClearanceTarget> rows,
        IReadOnlyList<ClearanceFloor> floors,
        EquatableArray<Region> ground,
        RetiredGround rg
    )
    {
        var output = new List<string>();
        foreach (var row in rows)
        {
            if (ground.Items.Any(g => GroundTarget(row, g, rg)))
            {
                output.Add(row.EntityID);
            }
        }

        foreach (var f in floors)
        {
            if (ground.Items.Any(g => g.Contains(f.Cell) && !GroundCovered(f.Cell, rows) && !(rg.Has(g) && rg.IsKept(f.Cell))))
            {
                output.Add(GroundFloorID(f.Cell));
            }
        }

        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>Names one floor cell as a clearance deficit target.</summary>
    public static string GroundFloorID(Cell c) => $"floor@{c.X},{c.Z}";

    /// <summary>A cell under a building the step left standing (a planned ring wall): its floor stays.</summary>
    private static bool GroundCovered(Cell c, IEnumerable<ClearanceTarget> rows) =>
        rows.Any(row => row.Player && c.X >= row.Minimum.X && c.X <= row.Maximum.X && c.Z >= row.Minimum.Z && c.Z <= row.Maximum.Z);

    /// <summary>
    /// The cells of every enclosed census room a wall target bounds that lies wholly inside the cleared
    /// ground: the roof the native deconstruct guard waits on. A room reaching outside is the guard's
    /// refusal, not a roof to remove.
    /// </summary>
    private static List<Cell> EnclosedRoof(IReadOnlyList<ClearanceTarget> walls, IReadOnlyList<Region> ground, RoomObservation rooms)
    {
        var seen = new HashSet<Cell>();
        var output = new List<Cell>();
        foreach (var room in rooms.Rooms.Items)
        {
            var (within, bounded, skip) = RoomWithinBounded(room, walls, ground);
            if (skip || !within || !bounded)
            {
                continue;
            }

            foreach (var c in room.Cells.Items)
            {
                if (seen.Add(c))
                {
                    output.Add(c);
                }
            }
        }

        return [.. output.OrderBy(c => c.X).ThenBy(c => c.Z)];
    }

    /// <summary>
    /// A wall target bounding an enclosed census room that reaches outside the cleared ground: opening it
    /// would expose a room that stays (the old footprint of a room the plan shrank or reshaped, until its new
    /// ring stands and splits it off).
    /// </summary>
    private static bool HoldsStandingRoom(IReadOnlyList<ClearanceTarget> walls, IReadOnlyList<Region> ground, RoomObservation rooms)
    {
        foreach (var room in rooms.Rooms.Items)
        {
            var (within, bounded, skip) = RoomWithinBounded(room, walls, ground);
            if (!skip && !within && bounded)
            {
                return true;
            }
        }

        return false;
    }

    private static (bool Within, bool Bounded, bool Skip) RoomWithinBounded(
        ObservedRoom room,
        IReadOnlyList<ClearanceTarget> walls,
        IReadOnlyList<Region> ground
    )
    {
        if ((room.Enclosed.TryGetValue(out var enclosed) && !enclosed) || room.Cells.Count == 0)
        {
            return (false, false, true);
        }

        bool within = true, bounded = false;
        foreach (var c in room.Cells.Items)
        {
            var isIn = ground.Any(g => g.Contains(c));
            within = within && isIn;
            foreach (var w in walls)
            {
                bounded = bounded || (c.X >= w.Minimum.X - 1 && c.X <= w.Maximum.X + 1 && c.Z >= w.Minimum.Z - 1 && c.Z <= w.Maximum.Z + 1);
            }
        }

        return (within, bounded, false);
    }

    internal static bool Inside(Cell c, Rectangle g) => c.X >= g.X && c.X < g.X + g.Width && c.Z >= g.Z && c.Z < g.Z + g.Height;

    internal static bool OnRing(Cell c, Rectangle g) => Inside(c, g) && (c.X == g.X || c.X == g.X + g.Width - 1 || c.Z == g.Z || c.Z == g.Z + g.Height - 1);

    private static bool Overlaps(ClearanceTarget row, Region g) =>
        g.Overlaps(
            RectRegion(
                new Rectangle
                {
                    X = row.Minimum.X,
                    Z = row.Minimum.Z,
                    Width = row.Maximum.X - row.Minimum.X + 1,
                    Height = row.Maximum.Z - row.Minimum.Z + 1,
                }
            )
        );
}
