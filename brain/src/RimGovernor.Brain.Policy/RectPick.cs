using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Rectangle picker: one picker over a candidate cell set (a plan field block, or a room interior) replaces
// the square patch search. It grows a solid run row by row nearest the anchor.
public sealed partial record LayoutPlan
{
    /// <summary>
    /// Each plan field zone as its own candidate set for PickRect: largest first, then nearest anchor, then by
    /// first cell.
    /// </summary>
    public IReadOnlyList<HashSet<Cell>> FieldBlocks(Cell anchor)
    {
        var blocks = new List<(HashSet<Cell> Cells, long Near, Cell First)>();
        foreach (var z in Zones)
        {
            if (z.Kind != ZoneKind.Field || z.Runs.Count == 0)
            {
                continue;
            }
            var cells = new HashSet<Cell>();
            long near = -1;
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    var c = new Cell(x, r.Z);
                    cells.Add(c);
                    var d = PolicyFunctions.RectDist(c, anchor);
                    if (near < 0 || d < near)
                    {
                        near = d;
                    }
                }
            }
            if (cells.Count == 0)
            {
                continue;
            }
            blocks.Add((cells, near, PolicyFunctions.RectSorted(cells)[0]));
        }
        return blocks
            .OrderByDescending(b => b.Cells.Count)
            .ThenBy(b => b.Near)
            .ThenBy(b => b.First.Z)
            .ThenBy(b => b.First.X)
            .Select(b => b.Cells)
            .ToList();
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Returns up to want cells of cells as a rectangle-ish block nearest anchor: it seeds at the candidate
    /// nearest anchor, takes a column window about sqrt(want) wide around the seed, and fills rows outward from
    /// the seed row (the anchor's side first), skipping cells outside the set. A window too narrow for want
    /// widens; the last resort is the remaining candidates nearest the anchor. want >= len(cells) returns the
    /// whole set. The result is row-major and deterministic.
    /// </summary>
    public static List<Cell> PickRect(IEnumerable<Cell> candidates, Cell anchor, int want)
    {
        var cells = candidates as HashSet<Cell> ?? new HashSet<Cell>(candidates);
        if (want <= 0 || cells.Count == 0)
        {
            return [];
        }
        var all = RectSorted(cells);
        if (want >= all.Count)
        {
            return all;
        }
        var seed = all[0];
        foreach (var c in all.Skip(1))
        {
            if (RectDist(c, anchor) < RectDist(seed, anchor))
            {
                seed = c;
            }
        }
        int minZ = seed.Z, maxZ = seed.Z, minX = seed.X, maxX = seed.X;
        foreach (var c in all)
        {
            minZ = Math.Min(minZ, c.Z);
            maxZ = Math.Max(maxZ, c.Z);
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X);
        }
        // Rows outward from the seed, the side nearer the anchor first.
        var rows = new List<int>();
        for (var d = 0; seed.Z - d >= minZ || seed.Z + d <= maxZ; d++)
        {
            int a = seed.Z - d, b = seed.Z + d;
            if (anchor.Z > seed.Z)
            {
                (a, b) = (b, a);
            }
            rows.Add(a);
            if (d > 0)
            {
                rows.Add(b);
            }
        }
        var w = 1;
        while (w * w < want)
        {
            w++;
        }
        HashSet<Cell> picked;
        for (; ; w++)
        {
            picked = RectWindow(cells, seed, anchor, rows, w, want);
            if (picked.Count >= want || w > maxX - minX)
            {
                break;
            }
        }
        if (picked.Count < want)
        {
            var rest = all.Where(c => !picked.Contains(c)).OrderBy(c => RectDist(c, anchor)).ToList();
            foreach (var c in rest.Take(want - picked.Count))
            {
                picked.Add(c);
            }
        }
        return RectSorted(picked);
    }

    /// <summary>
    /// Fills rows in order inside a w-wide column window about the seed (leaning to the anchor's side),
    /// columns nearest the seed first, taking a row only while it touches a row already taken.
    /// </summary>
    private static HashSet<Cell> RectWindow(HashSet<Cell> cells, Cell seed, Cell anchor, List<int> rows, int w, int want)
    {
        var lo = seed.X - (w / 2);
        if (anchor.X > seed.X)
        {
            lo = seed.X - ((w - 1) / 2);
        }
        var cols = new List<int>(w);
        for (var x = lo; x < lo + w; x++)
        {
            cols.Add(x);
        }
        cols = cols.OrderBy(x => Math.Abs(x - seed.X)).ThenBy(x => Math.Abs(x - anchor.X)).ToList();
        var output = new HashSet<Cell>();
        var taken = new HashSet<int>();
        foreach (var z in rows)
        {
            if (output.Count >= want)
            {
                break;
            }
            if (z != seed.Z && !taken.Contains(z - 1) && !taken.Contains(z + 1))
            {
                continue;
            }
            foreach (var x in cols)
            {
                var c = new Cell(x, z);
                if (cells.Contains(c) && output.Count < want)
                {
                    output.Add(c);
                    taken.Add(z);
                }
            }
        }
        return output;
    }

    internal static List<Cell> RectSorted(IEnumerable<Cell> set) =>
        set.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();

    internal static long RectDist(Cell a, Cell b)
    {
        long dx = a.X - b.X, dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
