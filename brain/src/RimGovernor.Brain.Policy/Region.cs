namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The region of every cell in r; empty when r has no area.</summary>
    public static Region RectRegion(Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            return new Region();
        }
        var runs = new List<RowRun>(r.Height);
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            runs.Add(new RowRun { Z = z, X = r.X, Length = r.Width });
        }
        return new Region { Runs = EquatableArray.From(runs) };
    }

    /// <summary>The region of the given cells; duplicates collapse.</summary>
    public static Region RegionOf(IReadOnlyList<Cell> cells)
    {
        var runs = new List<RowRun>(cells.Count);
        foreach (var c in cells)
        {
            runs.Add(new RowRun { Z = c.Z, X = c.X, Length = 1 });
        }
        return new Region { Runs = EquatableArray.From(CanonicalRuns(runs)) };
    }

    /// <summary>Canonicalizes arbitrary runs: unsorted, overlapping or touching runs merge, empty ones drop.</summary>
    public static Region RegionOfRuns(IReadOnlyList<RowRun> runs) =>
        new() { Runs = EquatableArray.From(CanonicalRuns([.. runs])) };

    internal static List<RowRun> CanonicalRuns(List<RowRun> runs)
    {
        var sorted = runs.OrderBy(r => r.Z).ThenBy(r => r.X).ToList();
        var output = new List<RowRun>();
        foreach (var r in sorted)
        {
            if (r.Length <= 0)
            {
                continue;
            }
            if (output.Count > 0)
            {
                var last = output[^1];
                if (last.Z == r.Z && r.X <= last.X + last.Length)
                {
                    var end = r.X + r.Length;
                    if (end > last.X + last.Length)
                    {
                        output[^1] = last with { Length = end - last.X };
                    }
                    continue;
                }
            }
            output.Add(r);
        }
        return output;
    }
}

/// <summary>
/// A cell set stored as row runs, the way LayoutZone stores a zone. Runs are canonical: rows ascending, runs
/// within a row ascending by X, no empty, overlapping or touching runs. Equal sets therefore compare equal.
/// The default Region is empty. A rectangle is one run per row.
/// </summary>
public sealed record Region
{
    public EquatableArray<RowRun> Runs { get; init; }

    /// <summary>Whether the region has no cells.</summary>
    public bool Empty() => Runs.Count == 0;

    /// <summary>The number of cells.</summary>
    public int Area()
    {
        var n = 0;
        foreach (var r in Runs)
        {
            n += r.Length;
        }
        return n;
    }

    /// <summary>The smallest rectangle holding every cell; zero when empty.</summary>
    public Rectangle Bounds()
    {
        if (Empty())
        {
            return new Rectangle();
        }
        int minX = Runs[0].X, maxX = Runs[0].X + Runs[0].Length;
        foreach (var r in Runs)
        {
            minX = Math.Min(minX, r.X);
            maxX = Math.Max(maxX, r.X + r.Length);
        }
        int minZ = Runs[0].Z, maxZ = Runs[^1].Z;
        return new Rectangle { X = minX, Z = minZ, Width = maxX - minX, Height = maxZ - minZ + 1 };
    }

    /// <summary>Whether the region fills its bounding rectangle. An empty region is not a rectangle.</summary>
    public bool IsRect()
    {
        var b = Bounds();
        return !Empty() && Area() == b.Width * b.Height;
    }

    /// <summary>Whether the cell is in the region.</summary>
    public bool Contains(Cell c)
    {
        // First run at or after the cell in (Z, X+Length) order, as Go's sort.Search.
        int lo = 0, hi = Runs.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var r = Runs[mid];
            if (r.Z > c.Z || (r.Z == c.Z && r.X + r.Length > c.X))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo < Runs.Count && Runs[lo].Z == c.Z && Runs[lo].X <= c.X;
    }

    /// <summary>Every cell sorted by X then Z, the order of rectCells.</summary>
    public IReadOnlyList<Cell> Cells()
    {
        if (Empty())
        {
            return [];
        }
        var output = new List<Cell>(Area());
        foreach (var r in Runs)
        {
            for (var x = r.X; x < r.X + r.Length; x++)
            {
                output.Add(new Cell(x, r.Z));
            }
        }
        return output.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
    }

    /// <summary>
    /// The ring around the region: every cell outside it that touches it orthogonally or diagonally
    /// (domain.RoomFootprint's wall rule).
    /// </summary>
    public Region Walls()
    {
        var runs = new List<RowRun>();
        foreach (var r in Runs)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                runs.Add(new RowRun { Z = r.Z + dz, X = r.X - 1, Length = r.Length + 2 });
            }
        }
        return new Region { Runs = EquatableArray.From(PolicyFunctions.CanonicalRuns(runs)) }.Minus(this);
    }

    /// <summary>The region's own edge: its cells that have an orthogonal neighbour outside the region.</summary>
    public Region Perimeter()
    {
        var output = new List<Cell>();
        foreach (var c in Cells())
        {
            foreach (var n in Neighbours(c))
            {
                if (!Contains(n))
                {
                    output.Add(c);
                    break;
                }
            }
        }
        return PolicyFunctions.RegionOf(output);
    }

    /// <summary>Whether the regions share a cell.</summary>
    public bool Overlaps(Region o)
    {
        int i = 0, j = 0;
        while (i < Runs.Count && j < o.Runs.Count)
        {
            RowRun a = Runs[i], b = o.Runs[j];
            if (a.Z != b.Z)
            {
                if (a.Z < b.Z)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }
            else if (a.X + a.Length <= b.X)
            {
                i++;
            }
            else if (b.X + b.Length <= a.X)
            {
                j++;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Shifts every cell by (dx, dz).</summary>
    public Region Translate(int dx, int dz)
    {
        if (Empty())
        {
            return new Region();
        }
        var runs = new List<RowRun>(Runs.Count);
        foreach (var r in Runs)
        {
            runs.Add(new RowRun { Z = r.Z + dz, X = r.X + dx, Length = r.Length });
        }
        return new Region { Runs = EquatableArray.From(runs) };
    }

    /// <summary>
    /// Whether c is outside the region and orthogonally next to one of its cells: a wall cell a door can
    /// stand in, not a diagonal corner.
    /// </summary>
    internal bool Adjacent(Cell c)
    {
        if (Contains(c))
        {
            return false;
        }
        foreach (var n in Neighbours(c))
        {
            if (Contains(n))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>This region without the cells of o. Both are canonical, so the result is.</summary>
    internal Region Minus(Region o)
    {
        var runs = new List<RowRun>();
        foreach (var r in Runs)
        {
            var x = r.X;
            foreach (var s in o.Runs)
            {
                if (s.Z != r.Z || s.X + s.Length <= x || s.X >= r.X + r.Length)
                {
                    continue;
                }
                if (s.X > x)
                {
                    runs.Add(new RowRun { Z = r.Z, X = x, Length = s.X - x });
                }
                x = Math.Max(x, s.X + s.Length);
            }
            if (x < r.X + r.Length)
            {
                runs.Add(new RowRun { Z = r.Z, X = x, Length = r.X + r.Length - x });
            }
        }
        return new Region { Runs = EquatableArray.From(runs) };
    }

    /// <summary>Whether both regions hold the same cells.</summary>
    public bool Equal(Region o) => Runs.Equals(o.Runs);

    /// <summary>
    /// The region dilated by n cells in every direction (Chebyshev): for a rectangle, the rectangle padded by
    /// n; for a ring, the ring thickened with its hole filled only as far as n reaches. Zero or negative n is
    /// the region itself.
    /// </summary>
    public Region Grow(int n)
    {
        if (n <= 0 || Empty())
        {
            return this;
        }
        var runs = new List<RowRun>(Runs.Count * ((2 * n) + 1));
        foreach (var r in Runs)
        {
            for (var dz = -n; dz <= n; dz++)
            {
                runs.Add(new RowRun { Z = r.Z + dz, X = r.X - n, Length = r.Length + (2 * n) });
            }
        }
        return new Region { Runs = EquatableArray.From(PolicyFunctions.CanonicalRuns(runs)) };
    }

    /// <summary>Whether the region shares a cell with r. A ring does not overlap a rectangle lying wholly in its hole.</summary>
    public bool OverlapsRect(Rectangle r) => Overlaps(PolicyFunctions.RectRegion(r));

    private static Cell[] Neighbours(Cell c) =>
        [new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1)];
}
