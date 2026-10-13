namespace RimGovernor.Brain.Policy;

// Door-to-door walkability. Pawns walk a room's free floor; a piece may not cut the aisle between its doors,
// wall off any floor, or stand on another piece's interaction cell. Reachability is over orthogonal steps,
// the conservative reading of RimWorld's no-corner-cutting pathing.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports whether adding a footprint to a room whose blocked cells are given keeps every door
    /// threshold and every free cell that was reachable from the doors reachable. A room already cut up by
    /// earlier furniture is judged only on what the new piece would take.
    /// </summary>
    public static bool InteriorPlacementWalkable(InteriorRoom room, IReadOnlySet<Cell> blocked, IReadOnlyList<Cell> footprint)
    {
        var thresholds = InteriorThresholds(room);
        var before = InteriorReach(room.Interior, blocked, thresholds);
        var after = new HashSet<Cell>(blocked);
        var placed = new HashSet<Cell>();
        foreach (var c in footprint)
        {
            after.Add(c);
            placed.Add(c);
        }
        foreach (var t in thresholds)
        {
            if (!blocked.Contains(t) && placed.Contains(t))
            {
                return false;
            }
        }
        var reach = InteriorReach(room.Interior, after, thresholds);
        foreach (var c in before)
        {
            if (!placed.Contains(c) && !reach.Contains(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The floor cells just inside the room's doors.</summary>
    internal static IReadOnlyList<Cell> InteriorThresholds(InteriorRoom room)
    {
        var r = room.Interior;
        var cells = new List<Cell>();
        foreach (var d in room.Doors)
        {
            var (side, ok) = DoorSide(r, d);
            if (!ok)
            {
                continue;
            }
            var o = RotateOffset(new Cell(0, 1), side);
            cells.Add(new Cell(d.X - o.X, d.Z - o.Z));
        }
        return cells;
    }

    /// <summary>Floods the room's unblocked floor from the free thresholds; the result holds a threshold only when every threshold joins the first free one.</summary>
    internal static IReadOnlySet<Cell> InteriorReach(Rectangle r, IReadOnlySet<Cell> blocked, IReadOnlyList<Cell> thresholds)
    {
        var reach = new HashSet<Cell>();
        var queue = new Queue<Cell>();
        foreach (var t in thresholds)
        {
            if (!blocked.Contains(t) && RectContains(r, t))
            {
                queue.Enqueue(t);
                break;
            }
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (reach.Contains(c) || blocked.Contains(c) || !RectContains(r, c))
            {
                continue;
            }
            reach.Add(c);
            queue.Enqueue(new Cell(c.X + 1, c.Z));
            queue.Enqueue(new Cell(c.X - 1, c.Z));
            queue.Enqueue(new Cell(c.X, c.Z + 1));
            queue.Enqueue(new Cell(c.X, c.Z - 1));
        }
        foreach (var t in thresholds)
        {
            if (!blocked.Contains(t) && !reach.Contains(t))
            {
                return new HashSet<Cell>();
            }
        }
        return reach;
    }

    internal static bool RectContains(Rectangle r, Cell c) => c.X >= r.X && c.Z >= r.Z && c.X < r.X + r.Width && c.Z < r.Z + r.Height;
}
