namespace RimGovernor.Brain.Policy;

// Roof support over the cell mirror: a pure port of the native RoofSupportGeometry.Blocker, so a batch of
// removals is validated as one joint counterfactual without a native call. A roofed cell stays up while a
// connected run of roofed cells (cardinal steps, each within Radius of the run's root) touches a roof-holding
// edifice that the removal leaves standing.

/// <summary>Names one building to remove by any cell it occupies.</summary>
public sealed record RemovedBuilding
{
    public Cell Cell { get; init; }

    public ulong Id { get; init; }
}

/// <summary>
/// What the check reads. Cell returns the mirror row; a cell inside Bounds with no row (null) is fogged.
/// Radius is the catalog's roof_max_support_distance. CollapsePending is optional: the mirror does not carry
/// the native collapse buffer, so null reads as nothing pending. Structural lists fogged cells a bounded
/// survey vouches for (a sealed shrine's interior): they are treated as known, never as a blanket bypass.
/// </summary>
public sealed record RoofSupportGrid
{
    public required Func<Cell, SiteCell?> Cell { get; init; }

    public Rectangle Bounds { get; init; }

    public double Radius { get; init; }

    public Func<Cell, bool>? CollapsePending { get; init; }

    public IReadOnlySet<Cell> Structural { get; init; } = new HashSet<Cell>();

    /// <summary>Open cells counted as holders the removal would find standing (the backups a stone shell will have built).</summary>
    public IReadOnlySet<Cell> AssumedHolders { get; init; } = new HashSet<Cell>();

    private static readonly (int X, int Z)[] CardinalSteps = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    internal bool InBounds(Cell c)
    {
        var b = Bounds;
        return c.X >= b.X && c.X < b.X + b.Width && c.Z >= b.Z && c.Z < b.Z + b.Height;
    }

    /// <summary>The cell's mirror row and whether the geometry there is unknown (fogged and not surveyed, or the roof fact unknown).</summary>
    private (SiteCell? Row, bool Unknown) Row(Cell c)
    {
        var row = Cell(c);
        if (row is null)
        {
            return (null, !Structural.Contains(c));
        }
        return (row, false);
    }

    private bool Roofed(Cell c)
    {
        var row = Cell(c);
        if (row is null)
        {
            // A fogged cell has no row; the caller classifies it unknown (or structural) and a surveyed
            // structural cell is roofed by construction.
            return true;
        }
        return row.Roofed.TryGetValue(out var v) && v;
    }

    private bool Holds(Cell c)
    {
        if (AssumedHolders.Contains(c))
        {
            return true;
        }
        var row = Cell(c);
        if (row is null)
        {
            return false;
        }
        return row.EdificeOf() is { } t && t.Has(ThingMarks.HoldsRoof);
    }

    internal bool Within(Cell a, Cell b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz) <= Radius * Radius;
    }

    /// <summary>
    /// Every cell the building with id occupies, found by walking cardinally connected cells that carry the
    /// same thing id from origin (the mirror lists a multi-cell building on each of its cells). Empty when
    /// origin holds no such thing.
    /// </summary>
    public IReadOnlyList<Cell> Footprint(Cell origin, ulong id)
    {
        bool Has(Cell c)
        {
            var row = Cell(c);
            return row is not null && row.Things.Any(t => t.Id == id && t.Category == ThingCategory.Building);
        }

        if (!Has(origin))
        {
            return [];
        }
        var seen = new HashSet<Cell> { origin };
        var output = new List<Cell> { origin };
        for (var i = 0; i < output.Count; i++)
        {
            foreach (var d in CardinalSteps)
            {
                var n = new Cell(output[i].X + d.X, output[i].Z + d.Z);
                if (!seen.Contains(n) && Has(n))
                {
                    seen.Add(n);
                    output.Add(n);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The verdict for removing every building in the batch at once: "" when every roof near them keeps a
    /// holder, else the first blocker. A building whose def holds no roof removes nothing the roof depends on
    /// and is skipped; a batch of only such buildings is "" too. Two buildings each safe alone can still block
    /// together, since each excludes the other as a holder.
    /// </summary>
    public (string Blocker, int CheckedRoofs) RoofBlocker(IReadOnlyList<RemovedBuilding> batch)
    {
        var excluded = new HashSet<Cell>();
        var removed = new List<Cell>();
        foreach (var b in batch)
        {
            var row = Cell(b.Cell);
            if (row is null)
            {
                return (PolicyFunctions.RoofBlockerUnknownGeom, 0);
            }
            var holder = false;
            foreach (var t in row.Things)
            {
                if (t.Id == b.Id && t.Category == ThingCategory.Building)
                {
                    holder = t.Has(ThingMarks.HoldsRoof);
                }
            }
            if (!holder)
            {
                continue;
            }
            foreach (var c in Footprint(b.Cell, b.Id))
            {
                if (excluded.Add(c))
                {
                    removed.Add(c);
                }
            }
        }
        if (removed.Count == 0)
        {
            return ("", 0);
        }
        return Blocker(removed);
    }

    /// <summary>The native RoofSupportGeometry.Blocker over explicit removed cells: every cell is excluded as a holder. "" means no unsupported roof.</summary>
    public (string Blocker, int CheckedRoofs) Blocker(IReadOnlyList<Cell> removed)
    {
        if (removed.Count == 0)
        {
            return (PolicyFunctions.RoofBlockerNoSupportCells, 0);
        }
        var excluded = new HashSet<Cell>(removed);
        var roots = new List<Cell>();
        var rootSet = new HashSet<Cell>();
        var r = (int)Radius;
        foreach (var c in removed)
        {
            for (var dz = -r - 1; dz <= r + 1; dz++)
            {
                for (var dx = -r - 1; dx <= r + 1; dx++)
                {
                    var near = new Cell(c.X + dx, c.Z + dz);
                    if (!Within(near, c))
                    {
                        continue;
                    }
                    if (!InBounds(near))
                    {
                        return (PolicyFunctions.RoofBlockerUnknownGeom, 0);
                    }
                    if (Row(near).Unknown)
                    {
                        return (PolicyFunctions.RoofBlockerUnknownGeom, 0);
                    }
                    if (Roofed(near) && rootSet.Add(near))
                    {
                        roots.Add(near);
                    }
                }
            }
        }
        var checkedRoofs = 0;
        foreach (var root in roots)
        {
            checkedRoofs++;
            if (CollapsePending is not null && CollapsePending(root))
            {
                return (PolicyFunctions.RoofBlockerCollapse, checkedRoofs);
            }
            var seen = new HashSet<Cell> { root };
            var queue = new Queue<Cell>();
            queue.Enqueue(root);
            bool supported = false, unknown = false;
            while (queue.Count > 0 && !supported)
            {
                var cell = queue.Dequeue();
                // adjacent-and-inside: the cell and its four neighbours.
                for (var i = -1; i < CardinalSteps.Length && !supported; i++)
                {
                    var near = cell;
                    if (i >= 0)
                    {
                        near = new Cell(cell.X + CardinalSteps[i].X, cell.Z + CardinalSteps[i].Z);
                    }
                    if (!InBounds(near) || !Within(near, root))
                    {
                        continue;
                    }
                    if (Row(near).Unknown)
                    {
                        unknown = true;
                        continue;
                    }
                    if (!excluded.Contains(near) && Holds(near))
                    {
                        supported = true;
                    }
                }
                foreach (var d in CardinalSteps)
                {
                    var next = new Cell(cell.X + d.X, cell.Z + d.Z);
                    if (!InBounds(next) || !Within(next, root) || !Roofed(next))
                    {
                        continue;
                    }
                    if (Row(next).Unknown)
                    {
                        unknown = true;
                        continue;
                    }
                    if (seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
            if (!supported)
            {
                return (unknown ? PolicyFunctions.RoofBlockerUnknownAlt : PolicyFunctions.RoofBlockerUnsupported, checkedRoofs);
            }
        }
        return ("", checkedRoofs);
    }
}

public static partial class PolicyFunctions
{
    // Roof blocker verdicts, the same words as the native check.
    public const string RoofBlockerNoSupportCells = "No occupied support cells";

    public const string RoofBlockerUnknownGeom = "Unknown building support geometry";

    public const string RoofBlockerUnknownAlt = "Alternate support geometry is unknown";

    public const string RoofBlockerCollapse = "Roof collapse is already pending";

    public const string RoofBlockerUnsupported = "Removing this building would leave unsupported roof";
}
