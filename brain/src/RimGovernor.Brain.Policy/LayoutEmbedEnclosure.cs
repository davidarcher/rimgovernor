namespace RimGovernor.Brain.Policy;

// The enclosure price of an unpriced room (#2987). A group the plan links by no flow (an outskirts room whose
// flows are not modelled) has no walk to price its distance, so it is not placed by the tie-break of the cell
// terms alone: it is still part of the base, and the base pays the wall around it and the ground that wall
// encloses. BlockCost therefore adds, for such a group, the marginal Wall and Footprint terms of PlanScore: a
// room far from the rest needs a ring of its own, one beside them shares the rest's, so scattering costs
// perimeter. Both weights are planWeights'; no constant is new.
//
// The terms are read off the rectangles, not the traced ring (coreEnclosure): the enclosure of a block is the
// block with its wall cells grown by the killbox yard (perimeterGap), the ring the cells within perimeterThick
// outside it, and a wall is built on the ring cells that are not impassable (PlanPerimeter's impassable: rock,
// hazard, unwalkable ground). The ring of a union is the union grown by the thickness less the union
// (ring(X) = grow(X) \ X), so adding block r to the others I costs
//
//   |walkable(grow(r) \ grow(I))| - |walkable(r \ I)|    wall cells
//   |pad(r, 1) \ pad(I, 1)|                              footprint cells
//
// (grow = by perimeterThick). Closing the outline, filling holes and the shoreline detours are left to the
// plan's real score; fields are outside the ring.
/// <summary>Rectangles sorted by X with Reach[i] the farthest right edge among Rects[0..i], so a scan starts at the first rectangle that can touch k.</summary>
internal sealed class XRects
{
    public XRects(IEnumerable<Rectangle> rects)
        : this(rects.ToArray())
    {
    }

    /// <summary>Sorts and keeps <paramref name="rects"/> itself; the caller hands it over.</summary>
    public XRects(Rectangle[] rects)
    {
        Rects = rects;
        Array.Sort(Rects, static (a, b) => a.X.CompareTo(b.X));
        Reach = new int[Rects.Length];
        var far = int.MinValue;
        for (var i = 0; i < Rects.Length; i++)
        {
            far = Math.Max(far, Rects[i].X + Rects[i].Width);
            Reach[i] = far;
        }
    }

    public Rectangle[] Rects { get; }

    public int[] Reach { get; }
}

/// <summary>The buffers one Uncovered call fills, kept across calls on one thread.</summary>
internal sealed class CoverScratch
{
    [ThreadStatic]
    private static CoverScratch? ThreadScratch;

    /// <summary>This thread's scratch: the seeds of one Embedder run in parallel, and no Uncovered call nests another.</summary>
    public static CoverScratch ForThread => ThreadScratch ??= new();

    private readonly List<Rectangle> _clipped = [];
    private List<Rectangle> _active = [];
    private List<Rectangle> _kept = [];
    private readonly List<int> _xs = [];
    private readonly List<(int Lo, int Hi)> _spans = [];

    /// <summary>The number of cells of k, weighted by weight (each counts 1 when null), that none of rects covers. k lies on the map. rects are sorted by X.</summary>
    public int Uncovered(Rectangle k, XRects rects, Prefix? weight)
    {
        int Free(int x0, int x1, int z0, int z1)
        {
            if (z1 <= z0)
            {
                return 0;
            }
            if (weight is null)
            {
                return (x1 - x0) * (z1 - z0);
            }
            return (int)weight.Sum(new Rectangle(x0, z0, x1 - x0, z1 - z0));
        }
        if (k.Width <= 0 || k.Height <= 0)
        {
            return 0;
        }
        _clipped.Clear();
        _xs.Clear();
        _xs.Add(k.X);
        _xs.Add(k.X + k.Width);
        // Rectangles whose reach does not pass k's left edge cannot touch it.
        var first = SearchReach(rects.Reach, k.X);
        for (var idx = first; idx < rects.Rects.Length; idx++)
        {
            var r = rects.Rects[idx];
            if (r.X >= k.X + k.Width)
            {
                break;
            }
            var c = LayoutPerimeterRules.ClipRect(r, k);
            if (c.Width > 0)
            {
                _clipped.Add(c);
                _xs.Add(c.X);
                _xs.Add(c.X + c.Width);
            }
        }
        _xs.Sort();
        var distinct = 0;
        for (var x = 0; x < _xs.Count; x++)
        {
            if (distinct == 0 || _xs[x] != _xs[distinct - 1])
            {
                _xs[distinct++] = _xs[x];
            }
        }
        _xs.RemoveRange(distinct, _xs.Count - distinct);
        var total = 0;
        // clipped is sorted by X and the strips run left to right, so a rectangle covers every strip from its
        // left edge to its right: the active ones are those started (X <= x0) and not yet passed (right edge >
        // x0; every right edge is a strip boundary, so one still active covers the whole strip).
        _active.Clear();
        var next = 0;
        for (var s = 0; s + 1 < _xs.Count; s++)
        {
            int x0 = _xs[s], x1 = _xs[s + 1];
            while (next < _clipped.Count && _clipped[next].X <= x0)
            {
                _active.Add(_clipped[next]);
                next++;
            }
            _kept.Clear();
            _spans.Clear();
            foreach (var c in _active)
            {
                if (c.X + c.Width <= x0)
                {
                    continue;
                }
                _kept.Add(c);
                // Insertion sort by start: a strip holds a handful of spans.
                var sp = (c.Z, c.Z + c.Height);
                _spans.Add(sp);
                var j = _spans.Count - 1;
                for (; j > 0 && _spans[j - 1].Lo > sp.Item1; j--)
                {
                    _spans[j] = _spans[j - 1];
                }
                _spans[j] = sp;
            }
            (_active, _kept) = (_kept, _active);
            var at = k.Z;
            foreach (var sp in _spans)
            {
                total += Free(x0, x1, at, Math.Min(sp.Lo, k.Z + k.Height));
                at = Math.Max(at, sp.Hi);
            }
            total += Free(x0, x1, at, k.Z + k.Height);
        }
        return total;
    }

    private static int SearchReach(int[] reach, int x)
    {
        int lo = 0, hi = reach.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (reach[mid] > x)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return lo;
    }
}

/// <summary>The rectangles the other blocks enclose, by X: each block grown by the yard, by the yard and the thickness, and by the wall cell. They depend on the group and the placed blocks, not the candidate, so a placement call builds them once.</summary>
internal sealed class EnclosureSet(XRects inRects, XRects grown, XRects walls)
{
    public XRects In { get; } = inRects;

    public XRects Grown { get; } = grown;

    public XRects Walls { get; } = walls;

    public int Calls { get; set; }

    /// <summary>Grown, in, walls: built after enough calls, over this thread's CoverGridBuffers while this set holds them.</summary>
    public CoverGrid[]? Grids { get; set; }
}

/// <summary>
/// The three prefix-sum arrays the cover grids of one thread are built in. Only the set that built its grids last
/// holds them: a set that takes them drops the grids of the one before, which rebuilds if it is priced again.
/// </summary>
internal sealed class CoverGridBuffers
{
    [ThreadStatic]
    private static CoverGridBuffers? ThreadBuffers;

    private readonly int[]?[] _arrays = new int[]?[3];
    private EnclosureSet? _owner;

    /// <summary>Builds s's grids (grown, in, walls) in this thread's arrays.</summary>
    public static CoverGrid[] Build(EnclosureSet s, Rectangle onMap, Prefix walkable)
    {
        var b = ThreadBuffers ??= new();
        if (b._owner is { } prior && prior != s)
        {
            prior.Grids = null;
        }
        b._owner = s;
        CoverGrid Grid(int k, XRects rects, Prefix? weight)
        {
            var grid = new CoverGrid(onMap, rects, weight, b._arrays[k]);
            b._arrays[k] = grid.Buffer;
            return grid;
        }
        return [Grid(0, s.Grown, walkable), Grid(1, s.In, walkable), Grid(2, s.Walls, null)];
    }
}

/// <summary>
/// The prefix sums of the cells a set of rectangles covers (each counting its weight, or 1 unweighted), so a
/// placement that prices many candidates against the same blocks reads the covered part of any k in O(1)
/// instead of sweeping the rectangles per candidate.
/// </summary>
internal sealed class CoverGrid
{
    private readonly Rectangle _bounds;
    private readonly int[] _s; // (Width+1) x (Height+1) prefix sums

    /// <summary>
    /// The grid over the bounding box of rects: nothing outside it is covered, so a query clipped to it reads the
    /// same sums as one over the whole map. weight (null: each cell counts 1) reads only cells on the map.
    /// </summary>
    public CoverGrid(Rectangle onMap, XRects rects, Prefix? weight, int[]? buffer = null)
    {
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        foreach (var r in rects.Rects)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }
            x0 = Math.Min(x0, r.X);
            z0 = Math.Min(z0, r.Z);
            x1 = Math.Max(x1, r.X + r.Width);
            z1 = Math.Max(z1, r.Z + r.Height);
        }
        if (x1 < x0)
        {
            x0 = z0 = x1 = z1 = 0;
        }
        _bounds = new Rectangle(x0, z0, x1 - x0, z1 - z0);
        var stride = _bounds.Width + 1;
        var size = stride * (_bounds.Height + 1);
        if (buffer is null || buffer.Length < size)
        {
            buffer = new int[size];
        }
        else
        {
            Array.Clear(buffer, 0, size);
        }
        _s = buffer;
        foreach (var r in rects.Rects)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }
            for (var z = r.Z; z < r.Z + r.Height; z++)
            {
                var baseIndex = ((z - z0 + 1) * stride) + 1 - x0;
                for (var x = r.X; x < r.X + r.Width; x++)
                {
                    _s[baseIndex + x] = 1;
                }
            }
        }
        for (var z = 0; z < _bounds.Height; z++)
        {
            for (var x = 0; x < _bounds.Width; x++)
            {
                var i = ((z + 1) * stride) + x + 1;
                var v = _s[i];
                if (v != 0 && weight is not null)
                {
                    v = 0;
                    var c = new Rectangle(x + x0, z + z0, 1, 1);
                    if (LayoutEmbedEnclosureRules.OnMapCell(onMap, c))
                    {
                        v = (int)weight.Sum(c);
                    }
                }
                _s[i] = v + _s[i - stride] + _s[i - 1] - _s[i - stride - 1];
            }
        }
    }

    /// <summary>The array the sums are in, at least as long as the grid needs; a later grid may be built in it.</summary>
    public int[] Buffer => _s;

    /// <summary>The covered weight inside k.</summary>
    public int Covered(Rectangle k)
    {
        k = LayoutPerimeterRules.ClipRect(k, _bounds);
        if (k.Width == 0)
        {
            return 0;
        }
        var stride = _bounds.Width + 1;
        int x0 = k.X - _bounds.X, z0 = k.Z - _bounds.Z;
        int x1 = x0 + k.Width, z1 = z0 + k.Height;
        return _s[(z1 * stride) + x1] - _s[(z0 * stride) + x1] - _s[(z1 * stride) + x0] + _s[(z0 * stride) + x0];
    }

    /// <summary>CoverScratch.Uncovered read off the grid: k's own weight less the covered.</summary>
    public int Uncovered(Rectangle k, Prefix? weight)
    {
        if (k.Width <= 0 || k.Height <= 0)
        {
            return 0;
        }
        if (weight is null)
        {
            return (k.Width * k.Height) - Covered(k);
        }
        return (int)weight.Sum(k) - Covered(k);
    }
}

internal sealed partial class Embedder
{
    /// <summary>
    /// Reports a room group none of whose links is a flow (positive weight): nothing prices its distance. A
    /// required link alone is a structural rule, not a trip.
    /// </summary>
    public bool Unpriced(int i)
    {
        if (IsField(i))
        {
            return false;
        }
        return !_groups[i].Links.Any(l => l.Weight > 0);
    }

    /// <summary>The EnclosureSet of group i against the other blocks.</summary>
    public EnclosureSet EnclosureRects(int i, IReadOnlyList<Rectangle> blocks)
    {
        const int yard = 1 + LayoutPerimeterRules.PerimeterGap; // the wall cells and the killbox yard
        bool Encloses(int j) => j != i && blocks[j].Width != 0 && !IsField(j);
        var n = 0;
        for (var j = 0; j < blocks.Count; j++)
        {
            if (Encloses(j))
            {
                n++;
            }
        }
        var inRects = new Rectangle[n];
        var grown = new Rectangle[n];
        var walls = new Rectangle[n];
        n = 0;
        for (var j = 0; j < blocks.Count; j++)
        {
            if (!Encloses(j))
            {
                continue;
            }
            var b = blocks[j];
            inRects[n] = LayoutPerimeterRules.Pad(b, yard);
            grown[n] = LayoutPerimeterRules.Pad(b, yard + LayoutPerimeterRules.PerimeterThick);
            walls[n] = LayoutPerimeterRules.Pad(b, 1);
            n++;
        }
        return new EnclosureSet(new XRects(inRects), new XRects(grown), new XRects(walls));
    }

    /// <summary>The Wall and Footprint cost of a group at r given the EnclosureSet of the other blocks.</summary>
    public double EnclosureCost(Rectangle r, EnclosureSet s)
    {
        const int yard = 1 + LayoutPerimeterRules.PerimeterGap; // the wall cells and the killbox yard
        var onMap = new Rectangle(0, 0, Base.W, Base.H);
        s.Calls++;
        if (s.Grids is null && s.Calls >= Math.Max(LayoutEmbedEnclosureRules.CoverGridMinCalls, onMap.Width * onMap.Height / LayoutEmbedEnclosureRules.CoverGridCellsPerCall))
        {
            s.Grids = CoverGridBuffers.Build(s, onMap, Walkable);
        }
        if (s.Grids is { } g)
        {
            var gridWall = g[0].Uncovered(LayoutPerimeterRules.ClipRect(LayoutPerimeterRules.Pad(r, yard + LayoutPerimeterRules.PerimeterThick), onMap), Walkable) -
                g[1].Uncovered(LayoutPerimeterRules.ClipRect(LayoutPerimeterRules.Pad(r, yard), onMap), Walkable);
            return (PlanWeights.Wall * gridWall) + (PlanWeights.Footprint * g[2].Uncovered(LayoutPerimeterRules.Pad(r, 1), null));
        }
        var wall = CoverScratch.ForThread.Uncovered(LayoutPerimeterRules.ClipRect(LayoutPerimeterRules.Pad(r, yard + LayoutPerimeterRules.PerimeterThick), onMap), s.Grown, Walkable) -
            CoverScratch.ForThread.Uncovered(LayoutPerimeterRules.ClipRect(LayoutPerimeterRules.Pad(r, yard), onMap), s.In, Walkable);
        return (PlanWeights.Wall * wall) + (PlanWeights.Footprint * CoverScratch.ForThread.Uncovered(LayoutPerimeterRules.Pad(r, 1), s.Walls, null));
    }
}

public static partial class LayoutEmbedEnclosureRules
{
    // A placement that prices more than CoverGridMinCalls candidates, or one a CoverGridCellsPerCall-th of the
    // map's cells, reads them off coverGrids built once: the grids cost a few passes over the map, a sweep
    // costs per candidate.
    internal const int CoverGridMinCalls = 64;
    internal const int CoverGridCellsPerCall = 1024;

    /// <summary>The number of cells of k, weighted by weight (each counts 1 when null), that none of rects covers. k lies on the map.</summary>
    internal static int Uncovered(Rectangle k, IEnumerable<Rectangle> rects, Prefix? weight) =>
        new CoverScratch().Uncovered(k, new XRects(rects), weight);

    internal static bool OnMapCell(Rectangle m, Rectangle c) =>
        c.X >= m.X && c.Z >= m.Z && c.X < m.X + m.Width && c.Z < m.Z + m.Height;

    /// <summary>Reports a cell a wall can be built on: PlanPerimeter's impassable is rock, hazard or ground nothing walks on (an unread cell is not impassable).</summary>
    internal static bool WalkableCell(IReadOnlyDictionary<Cell, SurveyCell> cells, Cell c) =>
        !cells.TryGetValue(c, out var sc) || !(sc.Rock || sc.Hazard || (!sc.Walkable && !sc.Built));
}
