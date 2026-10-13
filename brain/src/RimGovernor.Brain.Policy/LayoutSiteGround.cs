namespace RimGovernor.Brain.Policy;

/// <summary>A core set as a bool grid over its bounding box, so Column reads array cells instead of set entries.</summary>
public sealed class DenseCore
{
    private readonly int x0;
    private readonly int z0;
    private readonly int w;
    private readonly int h;
    private readonly bool[] inCore = [];

    public DenseCore(IEnumerable<Cell> core)
    {
        int ax = int.MaxValue, az = int.MaxValue, bx = int.MinValue, bz = int.MinValue;
        var cells = core.ToList();
        foreach (var c in cells)
        {
            (ax, az, bx, bz) = (Math.Min(ax, c.X), Math.Min(az, c.Z), Math.Max(bx, c.X), Math.Max(bz, c.Z));
        }
        if (bx < ax)
        {
            return;
        }
        (x0, z0, w, h) = (ax, az, bx - ax + 1, bz - az + 1);
        inCore = new bool[w * h];
        foreach (var c in cells)
        {
            inCore[((c.Z - z0) * w) + (c.X - x0)] = true;
        }
    }

    /// <summary>BuildGround.Column: the cells (x, z-CoreHalf..z+CoreHalf) all core.</summary>
    public bool Column(int x, int z)
    {
        if (x < x0 || x >= x0 + w || z - CoreHalf < z0 || z + CoreHalf >= z0 + h)
        {
            return false;
        }
        var i = ((z - CoreHalf - z0) * w) + (x - x0);
        for (var dz = 0; dz <= 2 * CoreHalf; dz++)
        {
            if (!inCore[i + (dz * w)])
            {
                return false;
            }
        }
        return true;
    }

    private static int CoreHalf => CoreHalf;
}

/// <summary>The map's size, its impassable cells and every cell's walking distance from the nearest open edge cell, shared by every candidate's wall and edge score.</summary>
public sealed record SiteGround
{
    public int W { get; init; }
    public int H { get; init; }
    public bool[] Blocked { get; init; } = [];

    /// <summary>Steps from an open edge cell; -1 when unreachable.</summary>
    public int[] Walk { get; init; } = [];

    /// <summary>
    /// One multi-source BFS over unblocked cells (8-connected, as a pawn walks) from every open cell on the map's
    /// outermost ring: raiders spawn only on open edge cells. dug are blocked cells a plan digs out, which then walk
    /// like open ground: a room in a mountain is as far from the edge as the walk to its entrance.
    /// </summary>
    public int[] EdgeWalk(IReadOnlySet<Cell>? dug)
    {
        var walk = new int[Blocked.Length];
        var queue = new List<int>();
        for (var i = 0; i < walk.Length; i++)
        {
            walk[i] = -1;
            int x = i % W, z = i / W;
            if (!Blocked[i] && (x == 0 || z == 0 || x == W - 1 || z == H - 1))
            {
                walk[i] = 0;
                queue.Add(i);
            }
        }
        for (var head = 0; head < queue.Count; head++)
        {
            var i = queue[head];
            int x = i % W, z = i / W;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H)
                    {
                        continue;
                    }
                    var n = (nz * W) + nx;
                    if (walk[n] >= 0 || (Blocked[n] && !(dug?.Contains(new Cell(nx, nz)) ?? false)))
                    {
                        continue;
                    }
                    walk[n] = walk[i] + 1;
                    queue.Add(n);
                }
            }
        }
        return walk;
    }

    /// <summary>The cell's walking distance from the nearest open edge cell, or -1 when it is off the map or no raider can reach it.</summary>
    public int WalkDist(Cell c) => c.X < 0 || c.X >= W || c.Z < 0 || c.Z >= H ? -1 : Walk[(c.Z * W) + c.X];
}

/// <summary>Ports <c>layout_site_ground.go</c>: siting seeds and the survey's shared ground state.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Search the best SiteSearchTop candidates for at most SiteSearchIters moves each. Iteration counts keep results deterministic; hourly replans use a separate, smaller budget.</summary>
    public const int SiteSearchTop = 8;

    public const int SiteSearchIters = 40;

    /// <summary>The coarse grid's spacing in cells.</summary>
    private const int SiteGridStep = 2;

    /// <summary>How far outside a rich patch an edge seed may sit.</summary>
    private const int SiteEdgeGap = 3;

    /// <summary>
    /// The candidate set, at most n seeds, every one passing column: the centroid seed, then an even share each of
    /// cells just outside a rich patch, cells along the rock edge and a coarse grid over the core ground, each stepped
    /// evenly through its (Z, X) order.
    /// </summary>
    internal static List<Cell> SiteSeeds(this BuildGround g, int n)
    {
        var output = new List<Cell>();
        var seen = new HashSet<Cell>();
        void Add(Cell c)
        {
            if (seen.Add(c))
            {
                output.Add(c);
            }
        }
        var (seed, ok) = g.Seed();
        if (ok)
        {
            Add(seed);
        }
        var edge = new List<Cell>();
        var rock = new List<Cell>();
        var grid = new List<Cell>();
        var dense = new DenseCore(g.Core);

        // NearRich is false everywhere when no cell is rich
        var anyRich = g.Soil.Values.Any(v => v == SoilCostRich);
        foreach (var c in g.Core)
        {
            if (!dense.Column(c.X, c.Z))
            {
                continue;
            }
            if (anyRich && g.Soil.GetValueOrDefault(c) != SoilCostRich && g.NearRich(c))
            {
                edge.Add(c);
            }
            if (!g.Rock.Contains(c) && g.BesideRock(c))
            {
                rock.Add(c);
            }
            if (c.X % SiteGridStep == 0 && c.Z % SiteGridStep == 0)
            {
                grid.Add(c);
            }
        }
        var share = Math.Max((n - 1) / 3, 1);
        foreach (var set in new[] { edge, rock, grid })
        {
            SortCells(set);
            foreach (var c in Stepped(set, share))
            {
                Add(c);
            }
        }
        return output.GetRange(0, Math.Min(n, output.Count));
    }

    private static readonly (int, int)[] Axis4 = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static bool NearRich(this BuildGround g, Cell c)
    {
        foreach (var (dx, dz) in Axis4)
        {
            for (var k = 1; k <= SiteEdgeGap; k++)
            {
                if (g.Soil.GetValueOrDefault(new Cell(c.X + (dx * k), c.Z + (dz * k))) == SoilCostRich)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool BesideRock(this BuildGround g, Cell c) => Axis4.Any(d => g.Rock.Contains(new Cell(c.X + d.Item1, c.Z + d.Item2)));

    internal static void SortCells(List<Cell> cs) => cs.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));

    /// <summary>Takes k cells evenly spaced through cs.</summary>
    internal static List<Cell> Stepped(List<Cell> cs, int k)
    {
        if (cs.Count <= k)
        {
            return cs;
        }
        var output = new List<Cell>(k);
        for (var i = 0; i < k; i++)
        {
            output.Add(cs[i * cs.Count / k]);
        }
        return output;
    }

    internal static SiteGround NewSiteGround(MapSurvey s)
    {
        int w = s.Bounds.Width, h = s.Bounds.Height;
        var b = new bool[Math.Max(w * h, 0)];
        foreach (var c in s.Cells)
        {
            if (c.Cell.X >= 0 && c.Cell.X < w && c.Cell.Z >= 0 && c.Cell.Z < h && (c.Rock || c.Hazard || (!c.Walkable && !c.Built)))
            {
                b[(c.Cell.Z * w) + c.Cell.X] = true;
            }
        }
        var g = new SiteGround { W = w, H = h, Blocked = b };
        return g with { Walk = g.EdgeWalk(null) };
    }

    /// <summary>
    /// Runs f for 0..n-1 across the available processors. This is deterministic: each f writes only its own slot, so
    /// the result never depends on scheduling or on how many workers run.
    /// </summary>
    internal static void EachIndex(int n, Action<int> f)
    {
        var workers = Math.Min(Environment.ProcessorCount, n);
        if (workers <= 1)
        {
            for (var i = 0; i < n; i++)
            {
                f(i);
            }
            return;
        }
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers }, f);
    }
}
