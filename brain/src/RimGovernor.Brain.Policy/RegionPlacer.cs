namespace RimGovernor.Brain.Policy;

/// <summary>
/// One kind of furniture a region should hold: Count pieces of Def, at least Min of them. Slot names the
/// pieces ("bench.0", "bench.1").
/// </summary>
public sealed record RegionWant
{
    public string Slot { get; init; } = "";

    public string Def { get; init; } = "";

    /// <summary>The definition's shape at North.</summary>
    public Cell Size { get; init; }

    /// <summary>The definition's interaction cell at North.</summary>
    public Cell? Interaction { get; init; }

    public int Count { get; init; }

    public int Min { get; init; }

    /// <summary>
    /// When set, names the pieces in placement order ("bed", "bunk.1") where the slot names are read by a
    /// consumer; piece i past the list is "Slot.i". Without it every piece is "Slot.i".
    /// </summary>
    public EquatableArray<string> Names { get; init; }

    /// <summary>
    /// Requires a footprint cell beside a cell outside the region (a wall, or the edge of a hole). Without it
    /// the piece still prefers a wall cell and falls back to the open floor.
    /// </summary>
    public bool AgainstWall { get; init; }

    /// <summary>
    /// Names another want (by Slot) whose pieces each piece of this one faces and touches, a chair at its
    /// table. It is a link the objective prices (FacilityLinks), not a veto: the greedy placer seats a piece
    /// beside its partner when it can, and only the pieces up to Min fall back to any free cell; a piece
    /// beyond Min with no partner to sit at is left out rather than stood alone.
    /// </summary>
    public string Beside { get; init; } = "";

    /// <summary>Keeps that many cells open in a straight line in front of the piece (where its user stands, behind its interaction cell), a horseshoes pin's throw lane.</summary>
    public int Clear { get; init; }

    /// <summary>
    /// Seats the piece where the most of the pieces that sit beside it (the wants whose Beside is this Slot)
    /// still fit, nearest the doors first, before any wall preference: a table with room for its chairs.
    /// </summary>
    public bool Open { get; init; }

    /// <summary>
    /// Names another want (by Slot) whose pieces each get one piece of this want on their interaction cell, a
    /// work chair at its bench: the worker stands at the chair, so the bench stays usable. The cell stays open
    /// floor to every other piece, and a seat blocks no walk (chairs are walked over), so the layout is the one
    /// without the seats. A seat is named "(partner slot)_chair"; a partner without an interaction cell, or
    /// whose cell holds a piece, gets none. Count caps the seats and Min is how many the want requires.
    /// </summary>
    public string OnInteraction { get; init; } = "";

    /// <summary>The name of the want's i-th piece.</summary>
    public string SlotName(int i) => i < Names.Count ? Names[i] : $"{Slot}.{i}";
}

/// <summary>
/// The input to PlaceInRegion. Doors are the cells outside the region that open into it; the region cell
/// beside each stays clear. Reserved cells take no piece. Wants place in order.
/// </summary>
public sealed record RegionPlacement
{
    public Region Region { get; init; } = new();

    public EquatableArray<Cell> Doors { get; init; }

    public EquatableArray<Cell> Reserved { get; init; }

    public EquatableArray<RegionWant> Wants { get; init; }

    /// <summary>
    /// When set, improves the greedy layout by the seeded interior search (SearchInterior) and returns the
    /// searched one, never worse than the greedy layout on a gated objective term. Null is the greedy layout.
    /// </summary>
    public InteriorSearch? Search { get; init; }
}

public static partial class PolicyFunctions
{
    private static readonly Rotation[] AllRotations = [Rotation.North, Rotation.East, Rotation.South, Rotation.West];

    private static IEnumerable<Cell> FourNeighbours(Cell c)
    {
        yield return new Cell(c.X + 1, c.Z);
        yield return new Cell(c.X - 1, c.Z);
        yield return new Cell(c.X, c.Z + 1);
        yield return new Cell(c.X, c.Z - 1);
    }

    /// <summary>
    /// The seats of w on the interaction cells of the pieces its OnInteraction names: one per partner up to
    /// Count, on a cell of the region no piece covers. A seat is a single cell.
    /// </summary>
    internal static List<InteriorPiece> SeatPieces(Region g, IReadOnlyList<InteriorPiece> pieces, RegionWant w)
    {
        var output = new List<InteriorPiece>();
        if (w.Size != new Cell(1, 1))
        {
            return output;
        }
        var covered = new HashSet<Cell>();
        foreach (var p in pieces)
        {
            foreach (var c in RectCells(p.Rect))
            {
                covered.Add(c);
            }
        }
        foreach (var p in pieces)
        {
            if (output.Count >= w.Count)
            {
                break;
            }
            var (c, ok) = p.Interaction();
            if (!ok || p.Row != w.OnInteraction || !g.Contains(c) || covered.Contains(c))
            {
                continue;
            }
            covered.Add(c);
            var seat = NewInteriorPiece(p.Slot + "_chair", w.Def, w.Size, Rotation.North, c) with { Row = w.Slot };
            output.Add(seat);
        }
        return output;
    }

    /// <summary>Separates the wants that stand on another want's interaction cell from the wants that take floor.</summary>
    internal static (List<RegionWant> Floor, List<RegionWant> Seats) SplitSeats(IEnumerable<RegionWant> wants)
    {
        var floor = new List<RegionWant>();
        var seats = new List<RegionWant>();
        foreach (var w in wants)
        {
            if (w.OnInteraction != "")
            {
                seats.Add(w);
            }
            else
            {
                floor.Add(w);
            }
        }
        return (floor, seats);
    }

    /// <summary>How many of the pieces that sit beside p the floor around it then holds, placed greedily on the cells next to it.</summary>
    internal static int SeatsAround(
        Region g,
        ISet<Cell> blocked,
        ISet<Cell> keep,
        IReadOnlyList<Cell> thresholds,
        IReadOnlyList<RegionWant> wants,
        InteriorPiece p)
    {
        RegionWant? dep = null;
        foreach (var w in wants)
        {
            if (w.Beside == p.Row)
            {
                dep = w;
                break;
            }
        }
        if (dep is null)
        {
            return 0;
        }
        var b = new HashSet<Cell>(blocked);
        var k = new HashSet<Cell>(keep);
        var ring = new List<Cell>();
        foreach (var c in RectCells(p.Rect))
        {
            b.Add(c);
            foreach (var n in Neighbours(c))
            {
                if (g.Contains(n) && !RectContains(p.Rect, n) && !ring.Contains(n))
                {
                    ring.Add(n);
                }
            }
        }
        foreach (var c in p.Reserves())
        {
            k.Add(c);
        }
        var placed = new List<InteriorPiece> { p };
        var seats = 0;
        while (seats < dep.Count)
        {
            var (s, ok) = NextRegionPiece(g, ring, b, k, thresholds, placed, [], dep, dep.SlotName(seats), false);
            if (!ok)
            {
                break;
            }
            foreach (var c in RectCells(s.Rect))
            {
                b.Add(c);
            }
            placed.Add(s);
            seats++;
        }
        return seats;
    }

    private static InteriorPiece RegionPiece(RegionWant w, string slot, Rotation rot, Cell corner)
    {
        var p = NewInteriorPiece(slot, w.Def, w.Size, rot, corner) with { Row = w.Slot, Beside = w.Beside, Clear = w.Clear };
        if (w.Interaction is { } off)
        {
            p = p with { InteractionOffset = off };
        }
        return p;
    }

    /// <summary>The want's Open piece: the fitting stand that seats the most pieces beside it, the nearest a door, then the first rotation and corner.</summary>
    internal static (InteriorPiece Piece, bool Ok) OpenRegionPiece(
        Region g,
        IReadOnlyList<Cell> cells,
        ISet<Cell> blocked,
        ISet<Cell> keep,
        ISet<Cell> before,
        IReadOnlyList<Cell> thresholds,
        IReadOnlyList<RegionWant> wants,
        RegionWant w,
        string slot)
    {
        InteriorPiece best = default!;
        var bestRing = -1;
        var bestDist = 0.0;
        var found = false;
        foreach (var rot in AllRotations)
        {
            foreach (var corner in cells)
            {
                var p = RegionPiece(w, slot, rot, corner);
                if (!RegionFits(g, blocked, keep, before, thresholds, p, false))
                {
                    continue;
                }
                var ring = SeatsAround(g, blocked, keep, thresholds, wants, p);
                var dist = 0.0;
                for (var i = 0; i < thresholds.Count; i++)
                {
                    var t = thresholds[i];
                    var d = CentreDistance(p.Rect, new Rectangle { X = t.X, Z = t.Z, Width = 1, Height = 1 });
                    if (i == 0 || d < dist)
                    {
                        dist = d;
                    }
                }
                if (!found || ring > bestRing || (ring == bestRing && dist < bestDist))
                {
                    best = p;
                    bestRing = ring;
                    bestDist = dist;
                    found = true;
                }
            }
        }
        return (best, found);
    }

    /// <summary>The cells just beyond the piece's footprint on the side it faces (rotation North faces +Z).</summary>
    internal static List<Cell> FrontCells(this InteriorPiece p)
    {
        var r = p.Rect;
        var output = new List<Cell>();
        switch (p.Rot)
        {
            case Rotation.North:
                for (var x = r.X; x < r.X + r.Width; x++)
                {
                    output.Add(new Cell(x, r.Z + r.Height));
                }
                break;
            case Rotation.South:
                for (var x = r.X; x < r.X + r.Width; x++)
                {
                    output.Add(new Cell(x, r.Z - 1));
                }
                break;
            case Rotation.East:
                for (var z = r.Z; z < r.Z + r.Height; z++)
                {
                    output.Add(new Cell(r.X + r.Width, z));
                }
                break;
            case Rotation.West:
                for (var z = r.Z; z < r.Z + r.Height; z++)
                {
                    output.Add(new Cell(r.X - 1, z));
                }
                break;
        }
        return output;
    }

    /// <summary>Whether the piece faces and touches one of the partners.</summary>
    internal static bool Faces(this InteriorPiece p, IReadOnlyList<InteriorPiece> partners)
    {
        foreach (var c in p.FrontCells())
        {
            foreach (var q in partners)
            {
                if (q.Row == p.Beside && RectContains(q.Rect, c))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The open cells the piece needs beside its footprint: its interaction cell and its clear lane.</summary>
    public static List<Cell> Reserves(this InteriorPiece p)
    {
        var output = new List<Cell>();
        var (ic, ok) = p.Interaction();
        if (ok)
        {
            output.Add(ic);
        }
        var a = p.Anchor();
        for (var k = 1; k <= p.Clear; k++)
        {
            var o = RotateOffset(new Cell(0, -k), p.Rot);
            var c = new Cell(a.X + o.X, a.Z + o.Z);
            if (!output.Contains(c))
            {
                output.Add(c);
            }
        }
        return output;
    }

    /// <summary>
    /// Fits furniture into any region: pieces stay on region cells, keep every door threshold clear, give each
    /// interaction cell open floor, and leave all floor that was reachable from the doors still reachable. The
    /// result is deterministic: wants in order, wall-touching anchors before open floor, rotations North, East,
    /// South, West, then row-major corners. Pieces carry world footprints. It throws when a want places fewer
    /// than its Min.
    /// </summary>
    public static List<InteriorPiece> PlaceInRegion(RegionPlacement req)
    {
        if (req.Search is { } search)
        {
            return SearchInterior(req, search).Stood();
        }
        var (floor, seats) = SplitSeats(req.Wants);
        req = req with { Wants = EquatableArray.From(floor) };
        var output = PlaceOnFloor(req);
        return SeatAll(req.Region, output, seats);
    }

    /// <summary>Adds the seat wants' pieces after the floor's, throwing when a want seats fewer than its Min.</summary>
    internal static List<InteriorPiece> SeatAll(Region g, List<InteriorPiece> pieces, IReadOnlyList<RegionWant> seats)
    {
        var output = new List<InteriorPiece>(pieces);
        foreach (var w in seats)
        {
            var placed = SeatPieces(g, pieces, w);
            if (placed.Count < w.Min)
            {
                throw new DomainException($"place in region: {w.Slot} seats {placed.Count} of the {w.Min} wanted");
            }
            output.AddRange(placed);
        }
        return output;
    }

    /// <summary>The greedy placement of the wants that take floor.</summary>
    internal static List<InteriorPiece> PlaceOnFloor(RegionPlacement req)
    {
        var g = req.Region;
        if (g.Empty())
        {
            throw new DomainException("place in region: empty region");
        }
        var blocked = new HashSet<Cell>(req.Reserved);
        var thresholds = new List<Cell>();
        foreach (var d in req.Doors)
        {
            if (g.Contains(d))
            {
                continue;
            }
            foreach (var n in FourNeighbours(d))
            {
                if (g.Contains(n))
                {
                    thresholds.Add(n);
                }
            }
        }
        if (req.Doors.Count > 0 && thresholds.Count == 0)
        {
            throw new DomainException("place in region: no door opens into the region");
        }
        var cells = g.Cells();
        var keep = new HashSet<Cell>(thresholds);
        var output = new List<InteriorPiece>();
        foreach (var w in req.Wants)
        {
            var placed = 0;
            while (placed < w.Count)
            {
                var (piece, ok) = NextRegionPiece(g, cells, blocked, keep, thresholds, output, req.Wants, w, w.SlotName(placed), placed < w.Min);
                if (!ok)
                {
                    break;
                }
                foreach (var c in RectCells(piece.Rect))
                {
                    blocked.Add(c);
                }
                foreach (var c in piece.Reserves())
                {
                    keep.Add(c);
                }
                output.Add(piece);
                placed++;
            }
            if (placed < w.Min)
            {
                throw new DomainException($"place in region: {w.Slot} fits {placed} of the {w.Min} wanted");
            }
        }
        return output;
    }

    internal static (InteriorPiece Piece, bool Ok) NextRegionPiece(
        Region g,
        IReadOnlyList<Cell> cells,
        ISet<Cell> blocked,
        ISet<Cell> keep,
        IReadOnlyList<Cell> thresholds,
        IReadOnlyList<InteriorPiece> placed,
        IReadOnlyList<RegionWant> wants,
        RegionWant w,
        string slot,
        bool required)
    {
        var before = RegionReach(g, blocked, thresholds);
        if (w.Open)
        {
            return OpenRegionPiece(g, cells, blocked, keep, before, thresholds, wants, w, slot);
        }
        // A want beside another seats at its partner first; only a piece the want requires may then take any free cell.
        var modes = new List<bool> { false };
        if (w.Beside != "")
        {
            modes = [true];
            if (required)
            {
                modes.Add(false);
            }
        }
        foreach (var beside in modes)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var wallPass = pass == 0;
                if (!wallPass && w.AgainstWall)
                {
                    break;
                }
                foreach (var rot in AllRotations)
                {
                    foreach (var corner in cells)
                    {
                        var p = RegionPiece(w, slot, rot, corner);
                        if (beside && !p.Faces(placed))
                        {
                            continue;
                        }
                        if (RegionFits(g, blocked, keep, before, thresholds, p, wallPass))
                        {
                            return (p, true);
                        }
                    }
                }
            }
        }
        return (default!, false);
    }

    internal static bool RegionFits(
        Region g,
        ISet<Cell> blocked,
        ISet<Cell> keep,
        ISet<Cell> before,
        IReadOnlyList<Cell> thresholds,
        InteriorPiece p,
        bool wallOnly)
    {
        var touches = false;
        var placed = new HashSet<Cell>();
        foreach (var c in RectCells(p.Rect))
        {
            if (!g.Contains(c) || blocked.Contains(c) || keep.Contains(c))
            {
                return false;
            }
            placed.Add(c);
            foreach (var n in FourNeighbours(c))
            {
                touches = touches || !g.Contains(n);
            }
        }
        if (wallOnly && !touches)
        {
            return false;
        }
        var reserves = p.Reserves();
        foreach (var c in reserves)
        {
            if (!g.Contains(c) || blocked.Contains(c) || placed.Contains(c))
            {
                return false;
            }
        }
        var after = new HashSet<Cell>(blocked);
        after.UnionWith(placed);
        var reach = RegionReach(g, after, thresholds);
        foreach (var c in before)
        {
            if (!placed.Contains(c) && !reach.Contains(c))
            {
                return false;
            }
        }
        if (thresholds.Count > 0)
        {
            foreach (var c in reserves)
            {
                if (!reach.Contains(c))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Floods the region's unblocked floor from the first free threshold; empty when some threshold does not join it.</summary>
    internal static HashSet<Cell> RegionReach(Region g, ISet<Cell> blocked, IReadOnlyList<Cell> thresholds)
    {
        var reach = new HashSet<Cell>();
        var queue = new Queue<Cell>();
        foreach (var t in thresholds)
        {
            if (!blocked.Contains(t))
            {
                queue.Enqueue(t);
                break;
            }
        }
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (reach.Contains(c) || blocked.Contains(c) || !g.Contains(c))
            {
                continue;
            }
            reach.Add(c);
            foreach (var n in FourNeighbours(c))
            {
                queue.Enqueue(n);
            }
        }
        foreach (var t in thresholds)
        {
            if (!blocked.Contains(t) && !reach.Contains(t))
            {
                return [];
            }
        }
        return reach;
    }
}
