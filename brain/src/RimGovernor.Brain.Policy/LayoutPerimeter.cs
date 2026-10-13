namespace RimGovernor.Brain.Policy;

/// <summary>A side position: a column across the ring.</summary>
internal readonly record struct Crossing(int Side, int Pos);

/// <summary>One side of the wall ring: positions Lo..Hi along Along, the outer face at Base(p), thickness toward Inward.</summary>
internal readonly record struct RingSide(Cell Inward, Cell Along, int Lo, int Hi, int Face, bool Vertical)
{
    public Cell Base(int p) => Vertical ? new Cell(Face, p) : new Cell(p, Face);

    public Cell At(int p, int t)
    {
        var b = Base(p);
        return new Cell(b.X + (Inward.X * t), b.Z + (Inward.Z * t));
    }
}

/// <summary>The distances one flood wrote (-1 where it did not reach).</summary>
internal sealed class FloodDist(FloodGrid g, int[] d)
{
    public FloodGrid G { get; } = g;
    public int[] D { get; } = d;

    public int? At(Cell c)
    {
        var i = G.Index(c);
        return i is { } k && D[k] >= 0 ? D[k] : null;
    }

    public Cell CellAt(int i) => new(G.X0 + (i % G.W), G.Z0 + (i / G.W));
}

/// <summary>The defense site's paths over a dense array, for the callers that need only distances: one pass per sector is the replan's hot loop.</summary>
internal sealed class FloodGrid
{
    private readonly bool[] pass = [];

    public FloodGrid()
    {
    }

    public FloodGrid(DefenseSite s)
    {
        if (s.Cells.Count == 0)
        {
            return;
        }
        var first = true;
        int x0 = 0, z0 = 0, x1 = 0, z1 = 0;
        foreach (var c in s.Cells.Keys)
        {
            if (first)
            {
                (x0, z0, x1, z1, first) = (c.X, c.Z, c.X, c.Z, false);
                continue;
            }
            (x0, z0, x1, z1) = (Math.Min(x0, c.X), Math.Min(z0, c.Z), Math.Max(x1, c.X), Math.Max(z1, c.Z));
        }
        (X0, Z0, W, H) = (x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        pass = new bool[W * H];
        foreach (var c in s.Cells.Keys)
        {
            if (s.Passable(c))
            {
                pass[((c.Z - Z0) * W) + c.X - X0] = true;
            }
        }
    }

    public int X0 { get; }
    public int Z0 { get; }
    public int W { get; }
    public int H { get; }

    public int? Index(Cell c)
    {
        if (c.X < X0 || c.Z < Z0 || c.X >= X0 + W || c.Z >= Z0 + H)
        {
            return null;
        }
        return ((c.Z - Z0) * W) + c.X - X0;
    }

    /// <summary>Floods the passable cells reachable from start.</summary>
    public FloodDist From(Cell start)
    {
        var d = new int[pass.Length];
        Array.Fill(d, -1);
        var output = new FloodDist(this, d);
        if (Index(start) is not { } at || !pass[at])
        {
            return output;
        }
        d[at] = 0;
        var queue = new List<int> { at };
        (int, int)[] steps = [(0, 1), (1, 0), (0, -1), (-1, 0)];
        for (var i = 0; i < queue.Count; i++)
        {
            var cur = queue[i];
            int x = cur % W, z = cur / W;
            foreach (var (sx, sz) in steps)
            {
                int nx = x + sx, nz = z + sz;
                if (nx < 0 || nz < 0 || nx >= W || nz >= H)
                {
                    continue;
                }
                var n = (nz * W) + nx;
                if (d[n] >= 0 || !pass[n])
                {
                    continue;
                }
                d[n] = d[cur] + 1;
                queue.Add(n);
            }
        }
        return output;
    }
}

/// <summary>
/// Ports <c>layout_perimeter.go</c>: the perimeter is a three-cell-thick ring around the core and its yard; rock and
/// deep water already close their cells, so only the open cells of a stretch are walled. Soft ground a raider wades
/// through (marsh, mud, shallow and moving water) is closed too: the wall follows the shore inside the ring when that
/// costs at most PerimeterDetour times the straight stretch, and otherwise crosses it, a wooden wall on light footing,
/// a bridge under the wall on water. Raider approaches from the map edges are traced with the defense planner's flood,
/// and the dry ring position most of them cross becomes the one opening: a killbox behind it, and a bent approach lane
/// outside. Airlock gates stand along the dry ring. A cover-clear band runs 30 cells out, and the mortar spot is the
/// firm cell farthest from the wall. Moisture pump sites inside the wall cover the soft ring cells a pump dries. The
/// colony's own buildings read as the ground under them, and a replan keeps the opening while it still opens.
/// </summary>
public static partial class PolicyFunctions
{
    public const int PerimeterThick = 3;

    /// <summary>The yard between the core and the wall; it holds the killbox and two cells behind its back wall.</summary>
    public const int PerimeterGap = KillboxDepth + 2;

    /// <summary>How far from the core a field patch counts as near it when scoring sites (SiteFieldReach).</summary>
    public const int PerimeterFieldReach = 15;

    public const int PerimeterGatePitch = 20;
    public const int PerimeterCoverBand = 30;
    public const int KillboxHalf = 7;
    public const int KillboxDepth = KillboxRows;
    public const int ApproachLeg = 8;

    /// <summary>Caps a shoreline detour's wall at this many times the straight stretch it replaces.</summary>
    public const double PerimeterDetour = 1.5;

    /// <summary>A moisture pump's reach, 6.9 cells, squared.</summary>
    public const int PumpRadiusSq = 47;

    private static readonly Cell[] Neighbours8Cells = [new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)];

    /// <summary>The smallest rectangle holding both cells.</summary>
    internal static Rectangle RectOf(Cell a, Cell b)
    {
        int x0 = Math.Min(a.X, b.X), z0 = Math.Min(a.Z, b.Z);
        return new Rectangle { X = x0, Z = z0, Width = Math.Max(a.X, b.X) - x0 + 1, Height = Math.Max(a.Z, b.Z) - z0 + 1 };
    }

    /// <summary>The rectangle grown by n cells on every side.</summary>
    public static Rectangle Pad(Rectangle r, int n) => new() { X = r.X - n, Z = r.Z - n, Width = r.Width + (2 * n), Height = r.Height + (2 * n) };

    /// <summary>The part of r inside to; the zero rectangle when none.</summary>
    public static Rectangle ClipRect(Rectangle r, Rectangle to)
    {
        int x0 = Math.Max(r.X, to.X), z0 = Math.Max(r.Z, to.Z);
        int x1 = Math.Min(r.X + r.Width, to.X + to.Width), z1 = Math.Min(r.Z + r.Height, to.Z + to.Height);
        if (x1 <= x0 || z1 <= z0)
        {
            return new Rectangle();
        }
        return new Rectangle { X = x0, Z = z0, Width = x1 - x0, Height = z1 - z0 };
    }

    /// <summary>Whether the cell lies in r.</summary>
    public static bool Contains(Rectangle r, Cell c) => c.X >= r.X && c.Z >= r.Z && c.X < r.X + r.Width && c.Z < r.Z + r.Height;

    /// <summary>
    /// Adds the wall, gates, killbox, approach, cover-clear band and mortar spot to plan (which needs its core rooms),
    /// replacing any it held. A plan without rooms comes back unchanged.
    /// </summary>
    public static LayoutPlan PlanPerimeter(LayoutPlan plan, MapSurvey s)
    {
        int w = s.Bounds.Width, h = s.Bounds.Height;
        if (plan.Rooms.Count == 0 || w < 1 || h < 1)
        {
            return plan;
        }
        var cells = new Dictionary<Cell, SurveyCell>();
        foreach (var c in s.Cells)
        {
            cells[c.Cell] = c;
        }
        bool Impassable(Cell c) => cells.TryGetValue(c, out var sc) && (sc.Rock || sc.Hazard || (!sc.Walkable && !sc.Built));
        bool Soft(Cell c) => cells.TryGetValue(c, out var sc) && sc.Soft();

        // The enclosure: the core's footprint and its yard (killbox included); the ring traced outside it.
        var enc = CoreEnclosure(plan, w, h);
        if (enc.Bbox.Width == 0)
        {
            return plan;
        }
        var (sides, owner) = enc.Sides();
        (int Side, int Pos) SideOf(Cell c) => owner.TryGetValue(c, out var x) ? (x.Side, x.Pos) : (-1, 0);

        // orphans are ring cells no side position holds (outline corners too tight for a full column); each is walled on its own.
        var orphans = enc.RingCells().Where(c => !owner.ContainsKey(c)).ToList();
        var ringBox = Pad(enc.Bbox, PerimeterThick);

        // built is a cell the core's rooms hold.
        bool Built(Cell c) => plan.Rooms.Any(r => Contains(Pad(r.Region.Bounds(), 1), c));

        // Soft ground on the ring. Each connected stretch of it is closed by a shoreline detour inside the ring where that stays short, else crossed where the ring runs.
        var wet = new HashSet<Crossing>();
        for (var k = 0; k < sides.Count; k++)
        {
            var sd = sides[k];
            for (var p = sd.Lo; p <= sd.Hi; p++)
            {
                for (var t = 0; t < PerimeterThick; t++)
                {
                    if (Soft(sd.At(p, t)))
                    {
                        wet.Add(new Crossing(k, p));
                    }
                }
            }
        }
        var box = ClipRect(Pad(ringBox, PerimeterGap), new Rectangle { Width = w, Height = h });
        var detour = new HashSet<Cell>(); // wall cells of kept detours
        var shut = new HashSet<Cell>(); // soft cells a kept detour leaves outside
        var detoured = new HashSet<Crossing>(); // wet positions a kept detour closes
        var failed = new HashSet<Crossing>(); // wet positions some stretch cannot detour
        var flooded = new HashSet<Cell>(); // soft cells already in some stretch
        foreach (var start in enc.RingCells())
        {
            if (!Soft(start) || flooded.Contains(start))
            {
                continue;
            }

            // The stretch: soft cells joined to this one inside box, diagonals included, since a pawn steps diagonally.
            var stretch = new List<Cell> { start };
            var queue = new Queue<Cell>();
            queue.Enqueue(start);
            flooded.Add(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in Neighbours8Cells)
                {
                    var n = AddCell(c, d);
                    if (Contains(box, n) && Soft(n) && flooded.Add(n))
                    {
                        stretch.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }
            var crossed = new HashSet<Crossing>();
            var inStretch = new HashSet<Cell>();
            foreach (var c in stretch)
            {
                inStretch.Add(c);
                var (kk, pp) = SideOf(c);
                if (kk >= 0)
                {
                    crossed.Add(new Crossing(kk, pp));
                }
            }

            // The detour: every cell inside the ring within the wall's thickness of the stretch. It fails on a room, the hallway, an unread cell or other soft ground.
            var wall = new HashSet<Cell>();
            var ok = true;
            foreach (var c in stretch)
            {
                if (enc.Inside(c) && Built(c))
                {
                    ok = false;
                }
                for (var dx = -PerimeterThick; dx <= PerimeterThick && ok; dx++)
                {
                    for (var dz = -PerimeterThick; dz <= PerimeterThick && ok; dz++)
                    {
                        var n = new Cell(c.X + dx, c.Z + dz);
                        if ((!enc.Inside(n) && !enc.OnRing(n)) || inStretch.Contains(n) || wall.Contains(n) || Impassable(n))
                        {
                            continue;
                        }
                        if (!cells.ContainsKey(n) || Built(n) || Soft(n))
                        {
                            ok = false;
                        }
                        wall.Add(n);
                    }
                }
            }
            var extra = 0;
            foreach (var c in wall)
            {
                var (kk, pp) = SideOf(c);
                if (kk < 0 || crossed.Contains(new Crossing(kk, pp)))
                {
                    extra++;
                }
            }
            if (!ok || extra > PerimeterDetour * PerimeterThick * crossed.Count)
            {
                failed.UnionWith(crossed);
                continue;
            }
            detour.UnionWith(wall);
            shut.UnionWith(stretch);
            detoured.UnionWith(crossed);
        }

        // An opening stands on dry ring positions, its killbox clear of any detour.
        (Rectangle Killbox, Func<int, int, Cell> At) OpeningAt(Crossing x)
        {
            var sd = sides[x.Side];
            var b = sd.Base(x.Pos);
            Cell At(int inward, int along) => new(b.X + (sd.Inward.X * inward) + (sd.Along.X * along), b.Z + (sd.Inward.Z * inward) + (sd.Along.Z * along));
            return (ClipRect(RectOf(At(PerimeterThick, -KillboxHalf), At(PerimeterThick + KillboxDepth - 1, KillboxHalf)), enc.Bbox), At);
        }

        // fields are the plan's field cells: a killbox on one would build over crops, so an opening off the patches is preferred.
        var fields = new HashSet<Cell>();
        foreach (var z in plan.Zones)
        {
            if (z.Kind != ZoneField)
            {
                continue;
            }
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    fields.Add(new Cell(x, r.Z));
                }
            }
        }
        bool Opens(Crossing x, bool strict)
        {
            var sd = sides[x.Side];
            if (x.Pos < sd.Lo + 1 || x.Pos > sd.Hi - 1)
            {
                return false;
            }
            for (var p = x.Pos - 1; p <= x.Pos + 1; p++)
            {
                if (wet.Contains(new Crossing(x.Side, p)))
                {
                    return false;
                }
                for (var t = 0; t < PerimeterThick; t++)
                {
                    if (detour.Contains(sd.At(p, t)))
                    {
                        return false;
                    }
                }
            }
            var (kb, at) = OpeningAt(x);

            // The killbox keeps its full width across the opening: a box the enclosure clips sideways leaves the opening off its centre, and no funnel reads from it.
            var mouth = RectOf(at(PerimeterThick, -KillboxHalf), at(PerimeterThick, KillboxHalf));
            if (ClipRect(mouth, kb) != mouth)
            {
                return false;
            }
            foreach (var c in RectCells(kb))
            {
                if (!enc.Inside(c) || (strict && fields.Contains(c)) || Built(c))
                {
                    return false;
                }
            }
            if (detour.Any(c => Contains(kb, c)) || shut.Any(c => Contains(kb, c)))
            {
                return false;
            }

            // Raiders fight through the killbox: no utility stands in it.
            return !plan.Reservations.Any(r => r.Area.OverlapsRect(kb));
        }

        // snap moves a crossing to the nearest position on its side that opens: off the fields within half a killbox, else the nearest at all, on a patch rather than no opening.
        Crossing? Snap(Crossing x)
        {
            var sd = sides[x.Side];
            foreach (var strict in new[] { true, false })
            {
                var reach = sd.Hi - sd.Lo;
                if (strict)
                {
                    reach = Math.Min(reach, KillboxHalf);
                }
                for (var d = 0; d <= reach; d++)
                {
                    foreach (var p in new[] { x.Pos - d, x.Pos + d })
                    {
                        var y = new Crossing(x.Side, p);
                        if (Opens(y, strict))
                        {
                            return y;
                        }
                    }
                }
            }
            return null;
        }

        // Approaches: every edge sector's shortest route to the core, through the defense flood; the ring position most routes cross opens.
        var site = new DefenseSite(new DefenseRequest { Bounds = s.Bounds, Region = new Rectangle { Width = w, Height = h } });
        foreach (var c in s.Cells)
        {
            var pass = !Impassable(c.Cell);
            var row = new DefenseCell { Cell = c.Cell, Passable = Known(pass) };
            if (pass && site.OnBorder(c.Cell))
            {
                row = row with { EdgeReachable = Known(true) };
            }
            site.Cells[c.Cell] = row;
        }
        var mid = new Cell();
        if (plan.Core() is { } core)
        {
            mid = core;
        }
        else if (plan.Rooms.Count > 0)
        {
            mid = plan.Rooms[0].Door;
        }
        var (origin, found) = (mid, false);
        for (var x = enc.Bbox.X; x < enc.Bbox.X + enc.Bbox.Width; x++)
        {
            for (var z = enc.Bbox.Z; z < enc.Bbox.Z + enc.Bbox.Height; z++)
            {
                var c = new Cell(x, z);
                if (enc.Inside(c) && site.Passable(c) && (!found || SquaredDistance(c, mid) < SquaredDistance(origin, mid)))
                {
                    (origin, found) = (c, true);
                }
            }
        }
        var meets = new List<(Crossing At, int Dist)>();
        if (found)
        {
            var (sectors, _) = site.ComputeSectors(origin);
            var flood = new FloodGrid(site);
            var dist = flood.From(origin);
            foreach (var sec in sectors)
            {
                Cell best = default;
                var bestFound = false;
                var bestD = 0;
                foreach (var c in sec.Edge)
                {
                    if (dist.At(c) is { } d && (!bestFound || d < bestD))
                    {
                        (best, bestD, bestFound) = (c, d, true);
                    }
                }
                if (!bestFound)
                {
                    continue;
                }

                // The first ring cell on some shortest route, nearest the edge: a raider meets the wall there.
                var fromEdge = flood.From(best);
                Cell hit = default;
                var hitDe = 0;
                var hitOK = false;
                for (var i = 0; i < fromEdge.D.Length; i++)
                {
                    var de = fromEdge.D[i];
                    if (de < 0 || dist.D[i] < 0 || de + dist.D[i] != bestD)
                    {
                        continue;
                    }
                    var c = fromEdge.CellAt(i);
                    if (!enc.OnRing(c))
                    {
                        continue;
                    }
                    if (!hitOK || de < hitDe || (de == hitDe && DefenseCellLess(c, hit)))
                    {
                        (hit, hitDe, hitOK) = (c, de, true);
                    }
                }
                if (!hitOK)
                {
                    continue;
                }
                var (k, p) = SideOf(hit);
                if (k >= 0)
                {
                    meets.Add((new Crossing(k, p), bestD));
                }
            }
        }
        var open = new Crossing();
        var opened = false;
        var votes = new Dictionary<Crossing, int>();
        var length = new Dictionary<Crossing, int>();
        foreach (var m in meets)
        {
            if (Snap(m.At) is { } x)
            {
                votes[x] = votes.GetValueOrDefault(x) + 1;
                length[x] = length.GetValueOrDefault(x) + m.Dist;
            }
        }

        // The opening already planned stays while it still opens.
        var keptReason = "";
        foreach (var prev in plan.LiveSites(NodeKillbox))
        {
            for (var k = 0; k < sides.Count; k++)
            {
                var sd = sides[k];
                for (var p = sd.Lo; p <= sd.Hi && !opened; p++)
                {
                    if (OpeningAt(new Crossing(k, p)).Killbox == prev.Footprint.Bounds() && Opens(new Crossing(k, p), false))
                    {
                        keptReason = prev.Reason;
                        (open, opened) = (new Crossing(k, p), true);
                    }
                }
            }
        }
        var pinned = opened;
        if (!pinned)
        {
            foreach (var (x, v) in votes)
            {
                var o = votes.GetValueOrDefault(open);
                if (!opened || v > o || (v == o && (length[x] < length.GetValueOrDefault(open) || (length[x] == length.GetValueOrDefault(open) && (x.Side < open.Side || (x.Side == open.Side && x.Pos < open.Pos))))))
                {
                    (open, opened) = (x, true);
                }
            }
        }

        var openingReason = ReasonOpeningNone;
        if (pinned && keptReason != "")
        {
            // A kept opening keeps the reason it was chosen for, so a replan that changes nothing stores nothing new.
            openingReason = keptReason;
        }
        else if (opened)
        {
            openingReason = ReasonOpeningRoutes;
        }
        var sites = new Dictionary<NodeKind, List<Region>>();
        void AddSite(NodeKind kind, Rectangle r)
        {
            r = ClipRect(r, new Rectangle { Width = w, Height = h });
            if (r.Width > 0 && r.Height > 0)
            {
                if (!sites.TryGetValue(kind, out var list))
                {
                    sites[kind] = list = [];
                }
                list.Add(RectRegion(r));
            }
        }
        var killbox = new Rectangle();
        if (opened)
        {
            Func<int, int, Cell> at;
            (killbox, at) = OpeningAt(open);
            AddSite(NodeKillbox, killbox);

            // The approach leaves the opening straight out, then turns along the wall, so the lane breaks line of sight into the killbox.
            AddSite(NodeApproach, RectOf(at(-1, -1), at(-ApproachLeg, 1)));
            AddSite(NodeApproach, RectOf(at(-ApproachLeg + 2, 2), at(-ApproachLeg, ApproachLeg + 1)));

            // The fence closes the opening on its outer face, flush with the wall: the one lane stays the single way through.
            AddSite(NodeFence, RectOf(at(0, -1), at(0, 1)));
        }

        // Wall runs and gates along the dry ring, side by side. A run shorter than the pitch (a step of a squared-off diagonal) takes its gate only a pitch clear of every other, so a staircase is gated about as often as a straight side.
        var gates = new List<Rectangle>();
        var stepGates = new List<StepGate>();
        HashSet<Cell> walls = [], light = [], bridges = [], gaps = [];
        for (var k = 0; k < sides.Count; k++)
        {
            var sd = sides[k];
            var kk = k;
            var (g, sg) = WallRuns(sd, Impassable, walls, p =>
            {
                var terrain = true;
                for (var t = 0; t < PerimeterThick; t++)
                {
                    terrain = terrain && Impassable(sd.At(p, t));
                }
                return (opened && kk == open.Side && p >= open.Pos - 1 && p <= open.Pos + 1) || terrain || wet.Contains(new Crossing(kk, p));
            });
            gates.AddRange(g);
            stepGates.AddRange(sg);
        }
        gates = PitchGates(gates, stepGates, c => walls.Contains(c) || Impassable(c));
        foreach (var g in gates)
        {
            AddSite(NodeGate, g);
        }

        // Soft stretches: a detour's wall, else the ring crossing them.
        foreach (var c in detour)
        {
            var (k, p) = SideOf(c);
            if (k < 0 || wet.Contains(new Crossing(k, p)))
            {
                walls.Add(c);
            }
        }
        void CloseCell(Cell c)
        {
            var read = cells.TryGetValue(c, out var sc);
            if (Impassable(c))
            {
                return;
            }
            if (!read || sc!.Footing == Footing.Firm)
            {
                walls.Add(c);
            }
            else if (sc.Footing == Footing.Light)
            {
                walls.Add(c);
                light.Add(c);
            }
            else if (sc.Bridgeable)
            {
                walls.Add(c);
                bridges.Add(c);
            }
            else
            {
                gaps.Add(c);
            }
        }
        foreach (var x in wet)
        {
            if (detoured.Contains(x) && !failed.Contains(x))
            {
                continue;
            }
            for (var t = 0; t < PerimeterThick; t++)
            {
                CloseCell(sides[x.Side].At(x.Pos, t));
            }
        }

        // Orphan ring cells close one by one, but for soft ground a kept detour leaves outside.
        foreach (var c in orphans)
        {
            if (!shut.Contains(c))
            {
                CloseCell(c);
            }
        }
        Region InMap(HashSet<Cell> set) => RegionOf([.. SortedCells(set).Where(c => c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h)]);
        var ring = NewSite("perimeter-ring", NodeRing, InMap(walls), ReasonRingTraced);
        var facts = new RingFacts { Light = InMap(light), Bridged = InMap(bridges), Gaps = InMap(gaps), Opening = openingReason };

        // Moisture pumps: sites inside the wall, greedily covering the soft ring cells a pump dries, each within PumpRadiusSq of its pump.
        var dries = new HashSet<Cell>();
        foreach (var c in enc.RingCells())
        {
            if (Soft(c) && cells[c].Dries)
            {
                dries.Add(c);
            }
        }

        // A site already planned stays while it still dries something, the pump standing there (Built) or not: a re-survey never moves a pump.
        var pumps = new HashSet<Cell>();
        bool PumpSite(Cell c, bool kept) =>
            cells.TryGetValue(c, out var sc) && enc.Inside(c) && !sc.Hazard && ((sc.Walkable && !sc.Built) || (kept && sc.Built)) && !sc.Rock && sc.Footing == Footing.Firm
            && !Built(c) && !Contains(killbox, c) && !detour.Contains(c) && !shut.Contains(c);
        int Drying(Cell pump) => dries.Count(d => SquaredDistance(pump, d) <= PumpRadiusSq);
        void Cover(Cell pump)
        {
            pumps.Add(pump);
            dries.RemoveWhere(d => SquaredDistance(pump, d) <= PumpRadiusSq);
        }
        foreach (var old in plan.LiveSites(NodePump))
        {
            if (PumpSite(old.Origin, true) && Drying(old.Origin) > 0)
            {
                Cover(old.Origin);
            }
        }
        var candidates = new HashSet<Cell>();
        foreach (var c in dries)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                for (var dz = -6; dz <= 6; dz++)
                {
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if ((dx * dx) + (dz * dz) <= PumpRadiusSq && PumpSite(n, false))
                    {
                        candidates.Add(n);
                    }
                }
            }
        }
        while (dries.Count > 0)
        {
            Cell best = default;
            var most = 0;
            foreach (var c in candidates)
            {
                var n = Drying(c);
                if (n > most || (n == most && n > 0 && DefenseCellLess(c, best)))
                {
                    (best, most) = (c, n);
                }
            }
            if (most == 0)
            {
                break;
            }
            candidates.Remove(best);
            Cover(best);
        }
        foreach (var c in SortedCells(pumps))
        {
            AddSite(NodePump, RectOf(c, c));
        }

        // Cover-clear band: everything within PerimeterCoverBand outside the ring.
        var band = new HashSet<Cell>();
        for (var i = 0; i < enc.Dist.Length; i++)
        {
            if (enc.Dist[i] > PerimeterThick)
            {
                band.Add(new Cell(i % w, i / w));
            }
        }
        var coverBand = InMap(band);

        // Mortar: the firm cell deepest inside the wall, off rooms, hallway, killbox and detours.
        var outside = enc.In.Select(v => !v).ToArray();
        var deep = ChebyshevField(w, h, outside, w + h);
        var mortar = new Cell();
        var depth = -1;
        for (var z = enc.Bbox.Z; z < enc.Bbox.Z + enc.Bbox.Height; z++)
        {
            for (var x = enc.Bbox.X; x < enc.Bbox.X + enc.Bbox.Width; x++)
            {
                var c = new Cell(x, z);
                if (!cells.TryGetValue(c, out var sc) || !enc.Inside(c) || sc.Hazard || !sc.Walkable || sc.Rock || sc.Footing != Footing.Firm || Built(c) || Contains(killbox, c) || detour.Contains(c) || pumps.Contains(c))
                {
                    continue;
                }
                var d = deep[(z * w) + x];
                if (d > depth)
                {
                    (mortar, depth) = (c, d);
                }
            }
        }
        if (depth >= 0)
        {
            AddSite(NodeMortar, RectOf(mortar, mortar));
        }

        // The ring's inside is stored on the node: the rooms' flood, stopped by the band and the wall.
        var wallCells = new HashSet<Cell>();
        foreach (var region in new[] { ring.Footprint, facts.Gaps })
        {
            wallCells.UnionWith(region.Cells());
        }
        foreach (var g in sites.GetValueOrDefault(NodeGate) ?? [])
        {
            wallCells.UnionWith(g.Cells());
        }
        var (inner, sealedRegion) = TraceInterior(plan.Rooms, wallCells, band);
        ring = ring with { Ring = facts with { Inner = inner, Sealed = sealedRegion } };

        var rev = plan.RingRevision();
        plan = plan.WithoutPerimeterSites();
        var planSites = new List<PlannedSite>(plan.Sites) { ring };

        // Each kind in a stable order, its parts as ordinals, so the same inputs store the same nodes.
        foreach (var kind in new[] { NodeGate, NodeKillbox, NodeApproach, NodeFence, NodePump, NodeMortar })
        {
            var parts = sites.GetValueOrDefault(kind) ?? [];
            for (var i = 0; i < parts.Count; i++)
            {
                string id = $"{Wire.Name(kind)}-{i:D2}", reason = "";
                if (kind == NodeKillbox)
                {
                    (id, reason) = (Wire.Name(kind), openingReason);
                }
                planSites.Add(NewSite(id, kind, parts[i], reason) with { Anchored = kind == NodeKillbox });
            }
        }
        if (!coverBand.Empty())
        {
            planSites.Add(NewSite("cover-clear", NodeCoverClear, coverBand, ""));
        }
        return (plan with { Sites = planSites }).WithRevision(rev);
    }
}
