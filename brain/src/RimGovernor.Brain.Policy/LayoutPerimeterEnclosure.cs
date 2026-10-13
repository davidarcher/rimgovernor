namespace RimGovernor.Brain.Policy;

/// <summary>
/// The enclosure the wall is traced around: the padded core box grown by the yard (PerimeterGap, which holds the
/// killbox), closed by PerimeterThick so the outline has no notches, with its holes filled. The ring is every cell
/// within PerimeterThick outside it. Field patches and geothermal sites are left outside.
/// </summary>
internal sealed class Enclosure
{
    private readonly int w;
    private readonly int h;

    public Enclosure(bool[] inside, int w, int h)
    {
        this.w = w;
        this.h = h;
        In = inside;
        Ring = new bool[w * h];
        Dist = PolicyFunctions.ChebyshevField(w, h, inside, PolicyFunctions.PerimeterThick + PolicyFunctions.PerimeterCoverBand);
        for (var i = 0; i < Dist.Length; i++)
        {
            int x = i % w, z = i / w;
            if (inside[i])
            {
                Bbox = PolicyFunctions.UnionRect(Bbox, new Rectangle { X = x, Z = z, Width = 1, Height = 1 });
            }
            Ring[i] = Dist[i] >= 1 && Dist[i] <= PolicyFunctions.PerimeterThick;
        }
    }

    /// <summary>The core and its yard.</summary>
    public bool[] In { get; }

    /// <summary>Within PerimeterThick outside In.</summary>
    public bool[] Ring { get; }

    /// <summary>Chebyshev distance from In, up to the cover band.</summary>
    public int[] Dist { get; }

    public Rectangle Bbox { get; }

    private int? Index(Cell c) => c.X < 0 || c.Z < 0 || c.X >= w || c.Z >= h ? null : (c.Z * w) + c.X;

    public bool Inside(Cell c) => Index(c) is { } i && In[i];

    public bool OnRing(Cell c) => Index(c) is { } i && Ring[i];

    /// <summary>Lists the ring row by row.</summary>
    public List<Cell> RingCells()
    {
        var output = new List<Cell>();
        for (var i = 0; i < Ring.Length; i++)
        {
            if (Ring[i])
            {
                output.Add(new Cell(i % w, i / w));
            }
        }
        return output;
    }

    /// <summary>
    /// Cuts the ring into straight sides, one per straight stretch of the enclosure's outline: south and north sides run
    /// on past a convex corner to own it, as on a rectangle. Each ring cell belongs to at most one side position (a full
    /// PerimeterThick column); a position whose column is not wholly ring, or is partly another side's, is left out and
    /// its cells are walled one by one.
    /// </summary>
    public (List<RingSide> Sides, Dictionary<Cell, Crossing> Owner) Sides()
    {
        var raw = new List<RingSide>();
        (Cell Out, bool Vertical)[] dirs = [(new Cell(0, -1), false), (new Cell(0, 1), false), (new Cell(1, 0), true), (new Cell(-1, 0), true)];
        foreach (var (outward, vertical) in dirs)
        {
            var inward = new Cell(-outward.X, -outward.Z);
            var along = vertical ? new Cell(0, 1) : new Cell(1, 0);

            // Rows (or columns) across, positions along.
            var (across, alongN) = vertical ? (w, h) : (h, w);
            Cell CellOf(int a, int p) => vertical ? new Cell(a, p) : new Cell(p, a);
            bool Edge(int a, int p)
            {
                var c = CellOf(a, p);
                return Inside(c) && !Inside(PolicyFunctions.AddCell(c, outward));
            }
            for (var a = 0; a < across; a++)
            {
                for (var p = 0; p < alongN; p++)
                {
                    if (!Edge(a, p))
                    {
                        continue;
                    }
                    var lo = p;
                    while (p + 1 < alongN && Edge(a, p + 1))
                    {
                        p++;
                    }
                    var hi = p;
                    if (!vertical)
                    {
                        if (!Inside(CellOf(a, lo - 1)))
                        {
                            lo -= PolicyFunctions.PerimeterThick;
                        }
                        if (!Inside(CellOf(a, hi + 1)))
                        {
                            hi += PolicyFunctions.PerimeterThick;
                        }
                    }
                    var face = a + (PolicyFunctions.PerimeterThick * (outward.X + outward.Z));
                    raw.Add(new RingSide(inward, along, lo, hi, face, vertical));
                }
            }
        }
        var owned = new HashSet<Cell>();
        var output = new List<RingSide>();
        var owner = new Dictionary<Cell, Crossing>();
        foreach (var sd in raw)
        {
            var start = -1;
            void Flush(int end)
            {
                if (start >= 0)
                {
                    var k = output.Count;
                    var s = sd with { Lo = start, Hi = end };
                    output.Add(s);
                    for (var p = start; p <= end; p++)
                    {
                        for (var t = 0; t < PolicyFunctions.PerimeterThick; t++)
                        {
                            owner[s.At(p, t)] = new Crossing(k, p);
                        }
                    }
                }
                start = -1;
            }
            for (var p = sd.Lo; p <= sd.Hi; p++)
            {
                var full = true;
                for (var t = 0; t < PolicyFunctions.PerimeterThick; t++)
                {
                    var c = sd.At(p, t);
                    full = full && OnRing(c) && !owned.Contains(c);
                }
                if (!full)
                {
                    Flush(p - 1);
                    continue;
                }
                for (var t = 0; t < PolicyFunctions.PerimeterThick; t++)
                {
                    owned.Add(sd.At(p, t));
                }
                if (start < 0)
                {
                    start = p;
                }
            }
            Flush(sd.Hi);
        }
        return (output, owner);
    }
}

/// <summary>
/// The traced enclosure read back from a plan's reservations: the cells the ring closes in, not its bounding box. The
/// cover-clear band lies wholly outside the ring, so a flood from the rooms that the band and the wall stop is the
/// inside; cells within PerimeterThick of the band are the ring itself (the opening and terrain stretches included) and
/// are left out.
/// </summary>
internal sealed class WallInterior
{
    /// <summary>The ring's bounds grown by the band.</summary>
    public Rectangle Box { get; set; }

    /// <summary>Strictly inside the ring.</summary>
    public bool[] In { get; set; } = [];

    /// <summary>Inside or on the ring.</summary>
    public bool[] Closed { get; set; } = [];

    public int? At(Cell c) => PolicyFunctions.Contains(Box, c) ? ((c.Z - Box.Z) * Box.Width) + c.X - Box.X : null;

    /// <summary>Lists the inside row by row.</summary>
    public List<Cell> Cells()
    {
        var output = new List<Cell>();
        for (var i = 0; i < In.Length; i++)
        {
            if (In[i])
            {
                output.Add(new Cell(Box.X + (i % Box.Width), Box.Z + (i / Box.Width)));
            }
        }
        return output;
    }

    /// <summary>Each cell's Chebyshev distance from the ring or its inside, -1 beyond limit or off the box.</summary>
    public Func<Cell, int> Near(int limit)
    {
        var d = PolicyFunctions.ChebyshevField(Box.Width, Box.Height, Closed, limit);
        return c => At(c) is { } i ? d[i] : -1;
    }

    /// <summary>The cells of set (one flag per box cell) as a region.</summary>
    public Region RegionOfSet(bool[] set)
    {
        var cs = new List<Cell>();
        for (var i = 0; i < set.Length; i++)
        {
            if (set[i])
            {
                cs.Add(new Cell(Box.X + (i % Box.Width), Box.Z + (i / Box.Width)));
            }
        }
        return PolicyFunctions.RegionOf(cs);
    }
}

/// <summary>Ports <c>layout_perimeter_enclosure.go</c>: the enclosure, its sides and the interior flood.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Each cell's Chebyshev distance from src, -1 beyond limit.</summary>
    internal static int[] ChebyshevField(int w, int h, bool[] src, int limit)
    {
        var d = new int[src.Length];
        var q = new Queue<int>();
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i])
            {
                q.Enqueue(i);
            }
            else
            {
                d[i] = -1;
            }
        }
        while (q.Count > 0)
        {
            var i = q.Dequeue();
            if (d[i] >= limit)
            {
                continue;
            }
            int x = i % w, z = i / w;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    {
                        continue;
                    }
                    var j = (nz * w) + nx;
                    if (d[j] < 0)
                    {
                        d[j] = d[i] + 1;
                        q.Enqueue(j);
                    }
                }
            }
        }
        return d;
    }

    /// <summary>Traces the ring around inside.</summary>
    internal static Enclosure NewEnclosure(bool[] inside, int w, int h) => new(inside, w, h);

    /// <summary>The region grown by grow cells (Chebyshev).</summary>
    internal static bool[] GrowRegion(bool[] region, int w, int h, int grow)
    {
        var field = ChebyshevField(w, h, region, grow);
        return [.. field.Select(v => v >= 0)];
    }

    /// <summary>Clips grown to yard, closes it by PerimeterThick and fills its holes.</summary>
    internal static bool[] CloseRegion(bool[] grown, int w, int h, Rectangle yard)
    {
        for (var i = 0; i < grown.Length; i++)
        {
            grown[i] = grown[i] && Contains(yard, new Cell(i % w, i / w));
        }

        // Closing: dilate, then erode by as much.
        var d = ChebyshevField(w, h, grown, PerimeterThick);
        var output = new bool[grown.Length];
        for (var i = 0; i < d.Length; i++)
        {
            output[i] = d[i] < 0;
        }
        d = ChebyshevField(w, h, output, PerimeterThick);
        for (var i = 0; i < d.Length; i++)
        {
            output[i] = d[i] < 0 && Contains(yard, new Cell(i % w, i / w));
        }

        // Holes: whatever the map edge cannot reach around it.
        var seen = new bool[output.Length];
        var q = new Queue<int>();
        for (var i = 0; i < output.Length; i++)
        {
            int x = i % w, z = i / w;
            if (!output[i] && (x == 0 || z == 0 || x == w - 1 || z == h - 1))
            {
                seen[i] = true;
                q.Enqueue(i);
            }
        }
        while (q.Count > 0)
        {
            var i = q.Dequeue();
            int x = i % w, z = i / w;
            foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
            {
                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                {
                    continue;
                }
                var j = (nz * w) + nx;
                if (!output[j] && !seen[j])
                {
                    seen[j] = true;
                    q.Enqueue(j);
                }
            }
        }
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = output[i] || !seen[i];
        }
        return output;
    }

    /// <summary>Covers cells with rectangles: row runs, stacked runs of the same span merged.</summary>
    internal static List<Rectangle> CellRects(IReadOnlyCollection<Cell> set)
    {
        var rects = new List<Rectangle>();
        var open = new Dictionary<(int, int), int>(); // span -> rect index ending on the last row
        foreach (var r in CellRuns([.. set]))
        {
            var key = (r.X, r.Length);
            if (open.TryGetValue(key, out var i) && rects[i].Z + rects[i].Height == r.Z)
            {
                rects[i] = rects[i] with { Height = rects[i].Height + 1 };
                continue;
            }
            open[key] = rects.Count;
            rects.Add(new Rectangle { X = r.X, Z = r.Z, Width = r.Length, Height = 1 });
        }
        return rects;
    }

    /// <summary>
    /// Reads the ring node's stored inside back into a grid; null for a plan without a ring. grow widens the box past
    /// the band for callers measuring out from the ring.
    /// </summary>
    internal static WallInterior? PlanInterior(LayoutPlan plan, int grow)
    {
        var (ring, hasRing) = plan.Ring();
        if (!hasRing)
        {
            return null;
        }
        var wi = new WallInterior { Box = Pad(UnionRect(ring.Footprint.Bounds(), ring.Ring!.Sealed.Bounds()), PerimeterCoverBand + grow) };
        wi.In = new bool[wi.Box.Width * wi.Box.Height];
        wi.Closed = new bool[wi.Box.Width * wi.Box.Height];
        foreach (var c in ring.Ring.Inner.Cells())
        {
            if (wi.At(c) is { } i)
            {
                wi.In[i] = true;
            }
        }
        foreach (var c in ring.Ring.Sealed.Cells())
        {
            if (wi.At(c) is { } i)
            {
                wi.Closed[i] = true;
            }
        }
        return wi;
    }

    /// <summary>
    /// Floods the ground the ring closes in from rooms: the cover-clear band lies wholly outside the ring, so a flood
    /// that the band and the wall stop is the inside; cells within PerimeterThick of the band are the ring itself (the
    /// opening and terrain stretches included) and are left out of inner, though sealed holds them. wall is every cell
    /// the ring holds, gaps and gates included.
    /// </summary>
    internal static (Region Inner, Region Sealed) TraceInterior(IReadOnlyList<PlannedRoom> rooms, HashSet<Cell> wall, HashSet<Cell> band)
    {
        var ring = new Rectangle();
        foreach (var c in wall)
        {
            ring = UnionRect(ring, new Rectangle { X = c.X, Z = c.Z, Width = 1, Height = 1 });
        }
        if (ring.Width == 0)
        {
            return (new Region(), new Region());
        }
        var wi = new WallInterior { Box = Pad(ring, PerimeterCoverBand) };
        int w = wi.Box.Width, h = wi.Box.Height;
        var banded = new bool[w * h];
        foreach (var c in band)
        {
            if (wi.At(c) is { } i)
            {
                banded[i] = true;
            }
        }
        var nearBand = ChebyshevField(w, h, banded, PerimeterThick);
        wi.In = new bool[w * h];
        wi.Closed = new bool[w * h];
        var q = new Queue<int>();
        void Seed(Cell c)
        {
            if (wi.At(c) is { } i && !wi.In[i] && !banded[i] && !wall.Contains(c))
            {
                wi.In[i] = true;
                q.Enqueue(i);
            }
        }
        foreach (var r in rooms)
        {
            foreach (var c in RectCells(r.Region.Bounds()))
            {
                Seed(c);
            }
        }
        while (q.Count > 0)
        {
            var i = q.Dequeue();
            var c = new Cell(wi.Box.X + (i % w), wi.Box.Z + (i / w));
            foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                Seed(n);
            }
        }
        for (var i = 0; i < wi.In.Length; i++)
        {
            if (wi.In[i])
            {
                wi.Closed[i] = true;
                wi.In[i] = nearBand[i] < 0;
            }
        }
        foreach (var c in wall)
        {
            if (wi.At(c) is { } i)
            {
                wi.Closed[i] = true;
            }
        }
        return (wi.RegionOfSet(wi.In), wi.RegionOfSet(wi.Closed));
    }
}
