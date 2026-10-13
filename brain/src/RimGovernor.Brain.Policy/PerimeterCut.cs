using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// The perimeter as a routing cut (#2973). The wall is about routing, not a shape: the cheapest set of wall
// cells such that every walk from a map edge to the base crosses a wall, or passes the killbox opening.
// Terrain that a pawn cannot cross (rock, lava, deep water) is a free barrier, so the cut uses it first; only
// walls count as barriers (not sandbags, not traps).
//
// Ordinary raiders path ByPawn, where a wall cell is impassable (PathFinderJob prices it 10000): they never
// breach while any route exists and walk to the opening however long the detour, so one unbroken cell of wall
// already funnels them. A diagonal chain of wall cells is sealed, since PathFinderJob refuses a diagonal step
// past a blocked orthogonal neighbour, so the cut is found on the four-connected walkable grid.
//
// Sappers (JobGiver_AISapper, PassAllDestroyableThings) price a destroyable wall cell at
// Cost_BlockedWallBase (70) plus 0.2 per hit point, on top of the step, and so weigh breaching against the
// detour to the opening; the cost of breaching is linear in the wall's thickness. The thickness of each
// stretch of the cut is the least that leaves the detour the cheaper way (SapperThickness). Breachers
// (BreachingGrid) price a wall at 20 + 0.015 per hit point, barely moved by thickness: a wall is not the
// defence against them, the killbox and turrets are. The raid generator rolls the sapper strategy
// (ImmediateAttackSappers, selection weight 0 below 700 raid points, 0.4 at 1000) for the factions with a
// canBeSapper pawn kind (pirates, mercenaries, tribes, the Empire, wasters), so early raids walk and later
// ones may tunnel; the thickness is designed for the later ones.
//
// The cut is advice beside the ring PlanPerimeter traces: the runtime only journals it (logPerimeterCut).

/// <summary>
/// The cut's one judgment table, registered in DefTables so a renamed def fails the dangling-reference gate.
/// WallDef is the building the cut is walled with. DetourBudgetTicks is the one judgment: the longest detour,
/// in ticks of walking, the thickness is designed against. The sapper formula alone would thicken a stretch
/// far from the opening without limit (a 250-cell map has detours of thousands of ticks); a stretch whose
/// detour is longer than the budget is designed for the budget and left breachable past it, which the killbox
/// and turrets answer. 1000 ticks is about 77 cells of walking, which makes the weakest wall (wood) four
/// thick and granite six: roughly twice today's three-thick ring where the detour is longest.
/// </summary>
public sealed record PerimeterTable(string WallDef, double DetourBudgetTicks);

/// <summary>
/// The wall's strength: the MaxHitPoints of the wall the cut is built from on firm ground (HP) and of the
/// wood wall laid on light footing and on a plain bridge (LightHP). An unknown strength leaves the thickness
/// at one cell.
/// </summary>
public sealed record PerimeterWall(Fact<double> HP, Fact<double> LightHP);

public static partial class PolicyConstants
{
    // The cut's reasons; the caller journals them as layout_plan rows with target perimeter_cut
    // (PerimeterCutDecisions).

    /// <summary>The cut closes every route but the opening.</summary>
    public const string ReasonCutTraced = "cut_traced";

    /// <summary>The walls cannot close a route (ground nothing can wall, or the opening's lane touches the base), so the cut leaves it.</summary>
    public const string ReasonCutOpen = "cut_unclosable";

    /// <summary>The thickness follows the stretch's detour to the opening.</summary>
    public const string ReasonThicknessDetour = "thickness_sapper_detour";

    /// <summary>The detour is past the budget; the thickness is designed for the budget.</summary>
    public const string ReasonThicknessCapped = "thickness_capped_at_budget";

    /// <summary>
    /// No walk along the outside reaches the opening, so no raider arrives there (an entry cell must reach
    /// the colony), and one wall is enough.
    /// </summary>
    public const string ReasonThicknessNoDetour = "thickness_no_route_to_opening";

    /// <summary>The wall's hit points are unknown.</summary>
    public const string ReasonThicknessUnknown = "thickness_hp_unknown";

    /// <summary>
    /// The cost of a cell nothing can wall: priced so the cut crosses it only when no other cut exists,
    /// never a hard veto.
    /// </summary>
    internal const int CutGap = 1000;

    internal const int FlowInf = 1 << 30;
}

/// <summary>A run of the cut's cells that share one thickness.</summary>
public sealed record CutSegment
{
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public int Thickness { get; init; }

    public double DetourTicks { get; init; }

    public double HP { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>One decision for the caller to journal.</summary>
public sealed record PerimeterCutDecision
{
    public string Verdict { get; init; } = "";

    public string Reason { get; init; } = "";

    public IReadOnlyDictionary<string, object> Attrs { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// The advised wall: Cut is the one-cell chain that closes the routes, Walls the cut thickened outward by its
/// segments, Unclosable the cut cells that stand on ground nothing can wall.
/// </summary>
public sealed record PerimeterCut
{
    public IReadOnlyList<Cell> Cut { get; init; } = [];

    public IReadOnlyList<Cell> Walls { get; init; } = [];

    public IReadOnlyList<Cell> Unclosable { get; init; } = [];

    public IReadOnlyList<CutSegment> Segments { get; init; } = [];

    public IReadOnlyList<PerimeterCutDecision> Decisions { get; init; } = [];
}

/// <summary>
/// The grid the cut is found on: a barrier is free, impassable terrain; cost is the price of walling a cell (0
/// where the cut may not stand); source cells are where raiders appear and sink cells are the base.
/// </summary>
internal sealed class CutGrid(int w, int h)
{
    public int W { get; } = w;

    public int H { get; } = h;

    public bool[] Barrier { get; } = new bool[w * h];

    public int[] Cost { get; } = new int[w * h];

    public bool[] Source { get; } = new bool[w * h];

    public bool[] Sink { get; } = new bool[w * h];

    public (int Index, bool Ok) Idx(Cell c)
    {
        if (c.X < 0 || c.Z < 0 || c.X >= W || c.Z >= H)
        {
            return (0, false);
        }
        return (c.Z * W + c.X, true);
    }

    public Cell CellAt(int i) => new(i % W, i / W);

    /// <summary>Calls f on the in-map orthogonal neighbours of i.</summary>
    public void Neighbours4(int i, Action<int> f)
    {
        int x = i % W, z = i / W;
        if (z > 0)
        {
            f(i - W);
        }
        if (x > 0)
        {
            f(i - 1);
        }
        if (x < W - 1)
        {
            f(i + 1);
        }
        if (z < H - 1)
        {
            f(i + W);
        }
    }
}

/// <summary>
/// A flow network in arc-pair form (arc e and e^1 are each other's reverse), solved by Dinic's algorithm. Arcs
/// are added in a fixed order, so the flow and the cut it yields are deterministic.
/// </summary>
internal sealed class FlowNet
{
    private readonly int[] _head;
    private readonly int[] _level;
    private readonly int[] _it;
    private readonly List<int> _next = [];
    private readonly List<int> _to = [];
    private readonly List<int> _cap = [];

    public FlowNet(int nodes)
    {
        _head = new int[nodes];
        _level = new int[nodes];
        _it = new int[nodes];
        Array.Fill(_head, -1);
    }

    public void Arc(int u, int v, int c)
    {
        _next.Add(_head[u]);
        _to.Add(v);
        _cap.Add(c);
        _head[u] = _to.Count - 1;
        _next.Add(_head[v]);
        _to.Add(u);
        _cap.Add(0);
        _head[v] = _to.Count - 1;
    }

    private bool Bfs(int s, int t)
    {
        Array.Fill(_level, -1);
        _level[s] = 0;
        var queue = new List<int> { s };
        for (var i = 0; i < queue.Count; i++)
        {
            var u = queue[i];
            for (var e = _head[u]; e >= 0; e = _next[e])
            {
                var v = _to[e];
                if (_cap[e] > 0 && _level[v] < 0)
                {
                    _level[v] = _level[u] + 1;
                    queue.Add(v);
                }
            }
        }
        return _level[t] >= 0;
    }

    /// <summary>
    /// One augmenting path from u to t along level edges, resuming at the current-arc pointers: the Go
    /// recursion, unrolled so a long path cannot overflow the stack.
    /// </summary>
    private int Dfs(int s, int t, int limit)
    {
        var path = new List<int>();
        var u = s;
        while (true)
        {
            if (u == t)
            {
                var pushed = limit;
                foreach (var e in path)
                {
                    pushed = Math.Min(pushed, _cap[e]);
                }
                foreach (var e in path)
                {
                    _cap[e] -= pushed;
                    _cap[e ^ 1] += pushed;
                }
                return pushed;
            }
            var advanced = false;
            while (_it[u] >= 0)
            {
                var e = _it[u];
                var v = _to[e];
                if (_cap[e] <= 0 || _level[v] != _level[u] + 1)
                {
                    _it[u] = _next[e];
                    continue;
                }
                path.Add(e);
                u = v;
                advanced = true;
                break;
            }
            if (advanced)
            {
                continue;
            }
            if (u == s)
            {
                return 0;
            }
            // Dead end: back up one arc and skip it.
            var back = path[^1];
            path.RemoveAt(path.Count - 1);
            u = _to[back ^ 1];
            _it[u] = _next[_it[u]];
        }
    }

    public long MaxFlow(int s, int t)
    {
        long flow = 0;
        while (Bfs(s, t))
        {
            Array.Copy(_head, _it, _head.Length);
            while (true)
            {
                var pushed = Dfs(s, t, PolicyConstants.FlowInf);
                if (pushed == 0)
                {
                    break;
                }
                flow += pushed;
            }
        }
        return flow;
    }

    /// <summary>The nodes that can still reach t in the residual network.</summary>
    public bool[] Reaching(int t)
    {
        var seen = new bool[_head.Length];
        seen[t] = true;
        var queue = new List<int> { t };
        for (var i = 0; i < queue.Count; i++)
        {
            var v = queue[i];
            for (var e = _head[v]; e >= 0; e = _next[e])
            {
                var u = _to[e];
                if (!seen[u] && _cap[e ^ 1] > 0)
                {
                    seen[u] = true;
                    queue.Add(u);
                }
            }
        }
        return seen;
    }
}

/// <summary>
/// A cut problem with its thickness inputs: Hp is the wall's hit points per cell (0 unknown), Mouth the
/// opening's outer cell the detour is walked to, and KeepOffWalls the cells thickening never walls (the
/// opening's channel and lane). HardToWall marks cells nothing can wall (priced CutGap).
/// </summary>
internal sealed class CutInput(CutGrid grid, double[] hp, bool[] hardToWall, bool[] keepOffWalls)
{
    public CutGrid Grid { get; } = grid;

    public double[] Hp { get; } = hp;

    public int Mouth { get; set; }

    public bool HasOpening { get; set; }

    public bool[] HardToWall { get; } = hardToWall;

    public bool[] KeepOffWalls { get; } = keepOffWalls;
}

public static partial class PolicyFunctions
{
    /// <summary>The eight cells around one.</summary>
    private static readonly Cell[] Neighbours8 = [new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)];

    private static readonly PerimeterTable PerimeterTableValue = new("Wall", 1000);

    /// <summary>The cut's judgment table.</summary>
    public static PerimeterTable PerimeterDefs() => PerimeterTableValue;

    /// <summary>What a sapper pays to take one wall cell down and step into it: Cost_BlockedWallBase plus 0.2 per hit point, plus the step.</summary>
    public static double SapperStepTicks(double hp) =>
        PathBlockedWallBase + (PathBlockedWallPerHp * hp) + PathMoveCardinal;

    /// <summary>
    /// The thickness of a stretch whose detour to the opening is detourTicks: the least N for which
    /// N*SapperStepTicks(hp) is at least the detour (capped at the table's budget), and at least one wall.
    /// </summary>
    public static int SapperThickness(double hp, double detourTicks)
    {
        if (hp <= 0)
        {
            return 1;
        }
        var d = Math.Min(detourTicks, PerimeterTableValue.DetourBudgetTicks);
        return Math.Max(1, (int)Math.Ceiling(d / SapperStepTicks(hp)));
    }

    /// <summary>
    /// Finds the cheapest set of wall cells separating the sources from the sink on the four-connected
    /// passable grid: a minimum vertex cut, by maximum flow with each cell split into an in and an out node
    /// whose arc costs the wall. Of the cuts of least cost it takes the one nearest the sink, so the wall
    /// hugs the base rather than the map edge. Flow is the cut's cost; a flow at FlowInf means no cut exists
    /// (a source touches the sink).
    /// </summary>
    private static (List<int> Cut, long Flow) RoutingCut(CutGrid g)
    {
        var n = g.W * g.H;
        int s = 2 * n, t = (2 * n) + 1;
        var net = new FlowNet((2 * n) + 2);
        for (var i = 0; i < n; i++)
        {
            if (g.Barrier[i])
            {
                continue;
            }
            var c = g.Cost[i];
            if (c == 0)
            {
                c = PolicyConstants.FlowInf;
            }
            net.Arc(2 * i, (2 * i) + 1, c);
            if (g.Source[i])
            {
                net.Arc(s, 2 * i, PolicyConstants.FlowInf);
            }
            if (g.Sink[i])
            {
                net.Arc((2 * i) + 1, t, PolicyConstants.FlowInf);
            }
            var from = i;
            g.Neighbours4(i, j =>
            {
                if (!g.Barrier[j])
                {
                    net.Arc((2 * from) + 1, 2 * j, PolicyConstants.FlowInf);
                }
            });
        }
        var flow = net.MaxFlow(s, t);
        var cut = new List<int>();
        if (flow >= PolicyConstants.FlowInf)
        {
            return (cut, flow);
        }
        var toSink = net.Reaching(t);
        for (var i = 0; i < n; i++)
        {
            if (!g.Barrier[i] && g.Cost[i] > 0 && !toSink[2 * i] && toSink[(2 * i) + 1])
            {
                cut.Add(i);
            }
        }
        return (cut, flow);
    }

    private struct CellDesign
    {
        public int N;
        public double Detour;
        public double Hp;
        public string Reason;
    }

    /// <summary>Finds the routing cut and its thickness.</summary>
    private static PerimeterCut DesignCut(CutInput input)
    {
        var g = input.Grid;
        var (cut, flow) = RoutingCut(g);
        if (flow >= PolicyConstants.FlowInf)
        {
            return new PerimeterCut
            {
                Decisions =
                [
                    new PerimeterCutDecision
                    {
                        Verdict = PolicyConstants.EmbedUnplaceable,
                        Reason = PolicyConstants.ReasonCutOpen,
                        Attrs = new Dictionary<string, object> { ["why"] = "source_touches_base" },
                    },
                ],
            };
        }
        var unclosable = new List<Cell>();
        var inCut = new bool[g.Barrier.Length];
        foreach (var i in cut)
        {
            inCut[i] = true;
            if (input.HardToWall[i])
            {
                unclosable.Add(g.CellAt(i));
            }
        }

        // Outside is what the sources reach with the cut standing.
        var outside = new bool[g.Barrier.Length];
        var queue = new List<int>();
        for (var i = 0; i < g.Source.Length; i++)
        {
            if (g.Source[i] && !g.Barrier[i] && !inCut[i])
            {
                outside[i] = true;
                queue.Add(i);
            }
        }
        for (var q = 0; q < queue.Count; q++)
        {
            g.Neighbours4(queue[q], j =>
            {
                if (!outside[j] && !g.Barrier[j] && !inCut[j] && !g.Sink[j])
                {
                    outside[j] = true;
                    queue.Add(j);
                }
            });
        }

        // The walk from the opening's mouth along the outside, in cells.
        var walk = new int[g.Barrier.Length];
        Array.Fill(walk, -1);
        if (input.HasOpening)
        {
            walk[input.Mouth] = 0;
            queue.Clear();
            queue.Add(input.Mouth);
            for (var q = 0; q < queue.Count; q++)
            {
                var u = queue[q];
                g.Neighbours4(u, j =>
                {
                    if (walk[j] < 0 && !g.Barrier[j] && !inCut[j] && !g.Sink[j] && outside[j])
                    {
                        walk[j] = walk[u] + 1;
                        queue.Add(j);
                    }
                });
            }
        }

        // Each cut cell's thickness from the detour of a raider that meets it.
        var design = new Dictionary<int, CellDesign>();
        foreach (var i in cut)
        {
            var best = -1;
            g.Neighbours4(i, j =>
            {
                if (outside[j] && walk[j] >= 0 && (best < 0 || walk[j] < best))
                {
                    best = walk[j];
                }
            });
            var hp = input.Hp[i];
            var d = new CellDesign { N = 1, Hp = hp, Reason = "" };
            if (hp <= 0)
            {
                d.Reason = PolicyConstants.ReasonThicknessUnknown;
            }
            else if (best < 0)
            {
                d.Reason = PolicyConstants.ReasonThicknessNoDetour;
            }
            else
            {
                d.Detour = best * PathMoveCardinal;
                d.N = SapperThickness(hp, d.Detour);
                d.Reason = PolicyConstants.ReasonThicknessDetour;
                if (d.Detour > PerimeterTableValue.DetourBudgetTicks)
                {
                    d.Reason = PolicyConstants.ReasonThicknessCapped;
                }
            }
            design[i] = d;
        }

        // Segments: runs of touching cut cells of one thickness and reason.
        cut.Sort();
        var segments = new List<CutSegment>();
        var done = new HashSet<int>();
        foreach (var i in cut)
        {
            if (done.Contains(i))
            {
                continue;
            }
            var thickness = design[i].N;
            var reason = design[i].Reason;
            var segCells = new List<Cell>();
            double detourTicks = 0;
            done.Add(i);
            var stack = new Stack<int>();
            stack.Push(i);
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                segCells.Add(g.CellAt(u));
                detourTicks = Math.Max(detourTicks, design[u].Detour);
                foreach (var d in Neighbours8)
                {
                    var c = AddCell(g.CellAt(u), d);
                    var (j, ok) = g.Idx(c);
                    if (ok && inCut[j] && !done.Contains(j) && design[j].N == thickness && design[j].Reason == reason)
                    {
                        done.Add(j);
                        stack.Push(j);
                    }
                }
            }
            segCells.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
            segments.Add(new CutSegment { Cells = segCells, Thickness = thickness, Reason = reason, HP = design[i].Hp, DetourTicks = detourTicks });
        }

        // The walls: the cut, thickened outward by each cell's thickness.
        var walls = new HashSet<Cell>();
        foreach (var i in cut)
        {
            var c = g.CellAt(i);
            walls.Add(c);
            var r = design[i].N - 1;
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dz = -r; dz <= r; dz++)
                {
                    var (j, ok) = g.Idx(new Cell(c.X + dx, c.Z + dz));
                    if (ok && outside[j] && g.Cost[j] > 0 && !input.KeepOffWalls[j])
                    {
                        walls.Add(g.CellAt(j));
                    }
                }
            }
        }
        var cutCells = cut.Select(g.CellAt).ToList();
        var wallCells = SortedCells(walls);

        var decisions = new List<PerimeterCutDecision>();
        var verdict = PolicyConstants.EmbedPlace;
        var why = PolicyConstants.ReasonCutTraced;
        if (unclosable.Count > 0)
        {
            (verdict, why) = (PolicyConstants.EmbedUnplaceable, PolicyConstants.ReasonCutOpen);
        }
        decisions.Add(new PerimeterCutDecision
        {
            Verdict = verdict,
            Reason = why,
            Attrs = new Dictionary<string, object>
            {
                ["cut"] = cutCells.Count,
                ["walls"] = wallCells.Count,
                ["unclosable"] = unclosable.Count,
                ["cost"] = flow,
            },
        });
        for (var k = 0; k < segments.Count; k++)
        {
            var seg = segments[k];
            decisions.Add(new PerimeterCutDecision
            {
                Verdict = PolicyConstants.EmbedPlace,
                Reason = seg.Reason,
                Attrs = new Dictionary<string, object>
                {
                    ["segment"] = k,
                    ["cells"] = seg.Cells.Count,
                    ["thickness"] = seg.Thickness,
                    ["detour_ticks"] = seg.DetourTicks,
                    ["hp"] = seg.HP,
                },
            });
        }
        return new PerimeterCut
        {
            Cut = cutCells,
            Walls = wallCells,
            Unclosable = unclosable,
            Segments = segments,
            Decisions = decisions,
        };
    }

    /// <summary>
    /// Advises the routing cut around the plan's core enclosure over the survey: the base is the enclosure
    /// (the core and its yard, the killbox included), the way in the plan's killbox opening, its channel and
    /// approach lane. A plan without rooms or an enclosure has no cut. The wall's strength sets the
    /// thickness.
    /// </summary>
    public static PerimeterCut PlanPerimeterCut(LayoutPlan plan, MapSurvey s, PerimeterWall wall)
    {
        int w = s.Bounds.Width, h = s.Bounds.Height;
        if (plan.Rooms.Count == 0 || w < 1 || h < 1)
        {
            return new PerimeterCut();
        }
        var enc = CoreEnclosure(plan, w, h);
        if (enc.Bbox.Width == 0)
        {
            return new PerimeterCut();
        }
        var g = new CutGrid(w, h);
        var input = new CutInput(g, new double[w * h], new bool[w * h], new bool[w * h]);
        var light = wall.HP.TryGetValue(out var firm);
        var lightKnown = wall.LightHP.TryGetValue(out var lightHp);
        if (!light)
        {
            firm = 0;
        }
        if (!lightKnown)
        {
            lightHp = 0;
        }
        var cells = new SurveyCell?[w * h];
        var read = new bool[w * h];
        foreach (var c in s.Cells)
        {
            var (i, ok) = g.Idx(c.Cell);
            if (ok)
            {
                cells[i] = c;
                read[i] = true;
            }
        }
        for (var i = 0; i < w * h; i++)
        {
            var sc = cells[i];
            if (read[i] && (sc!.Rock || sc.Hazard || (!sc.Walkable && !sc.Built)))
            {
                g.Barrier[i] = true;
                continue;
            }
            if (!read[i] || sc!.Footing == Footing.Firm)
            {
                g.Cost[i] = 1;
                input.Hp[i] = firm;
            }
            else if (sc.Footing == Footing.Light)
            {
                g.Cost[i] = 1;
                input.Hp[i] = lightHp;
            }
            else if (sc.Bridgeable)
            {
                g.Cost[i] = 2;
                input.Hp[i] = lightHp;
            }
            else
            {
                g.Cost[i] = PolicyConstants.CutGap;
                input.HardToWall[i] = true;
            }
            int x = i % w, z = i / w;
            g.Source[i] = x == 0 || z == 0 || x == w - 1 || z == h - 1;
        }
        for (var i = 0; i < g.Sink.Length; i++)
        {
            if (enc.In[i] && !g.Barrier[i])
            {
                g.Sink[i] = true;
                g.Cost[i] = 0;
                g.Source[i] = false;
            }
        }

        // The opening: its channel through the ring, the lane outside it.
        var (k, kok) = KillboxOpening(plan);
        if (kok)
        {
            var d = DirectionOf(k.Toward);
            var p = Perpendicular(d);
            for (var t = 0; t < PerimeterThick; t++)
            {
                for (var a = -1; a <= 1; a++)
                {
                    var (i, ok) = g.Idx(AddCell(AddCell(k.Entry, Scale(d, t)), Scale(p, a)));
                    if (ok)
                    {
                        g.Barrier[i] = true;
                        input.KeepOffWalls[i] = true;
                    }
                }
            }
            var (mouth, mouthOk) = g.Idx(k.Entry);
            if (mouthOk)
            {
                input.Mouth = mouth;
                input.HasOpening = true;
            }
            foreach (var a in plan.LiveSites(NodeKind.Approach))
            {
                foreach (var c in a.Footprint.Cells())
                {
                    var (i, ok) = g.Idx(c);
                    if (ok && !g.Barrier[i] && !g.Sink[i])
                    {
                        g.Cost[i] = 0;
                        g.Source[i] = true;
                        input.KeepOffWalls[i] = true;
                    }
                }
            }
        }
        return DesignCut(input);
    }

    /// <summary>The cut's decisions, for the caller to journal.</summary>
    public static IReadOnlyList<PerimeterCutDecision> PerimeterCutDecisions(PerimeterCut c) => c.Decisions;
}
