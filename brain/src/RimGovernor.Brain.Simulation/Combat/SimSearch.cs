using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>The reusable A* state. Cells are stamped with a generation so a search never clears the grid-sized arrays.</summary>
internal sealed class Search
{
    private uint _gen;

    public uint[] Seen { get; private set; } = [];

    public uint[] Closed { get; private set; } = [];

    public double[] GScore { get; private set; } = [];

    public int[] From { get; private set; } = [];

    /// <summary>The open list, ordered by f, then cell index, so equal-cost paths resolve the same way on every run.</summary>
    public PriorityQueue<int, (double F, int I)> Open { get; } = new(new NodeComparer());

    public List<int> Out { get; } = [];

    public uint Gen => _gen;

    public void Reset(int n)
    {
        if (Seen.Length != n)
        {
            Seen = new uint[n];
            Closed = new uint[n];
            GScore = new double[n];
            From = new int[n];
            _gen = 0;
        }
        _gen++;
        Open.Clear();
    }

    /// <summary>Walks the parents back from goal and returns the path start-exclusive.</summary>
    public List<int> Trace(int goal)
    {
        Out.Clear();
        for (var i = goal; From[i] >= 0; i = From[i])
        {
            Out.Add(i);
        }
        Out.Reverse();
        return Out;
    }

    private sealed class NodeComparer : IComparer<(double F, int I)>
    {
        public int Compare((double F, int I) a, (double F, int I) b)
        {
            if (a.F != b.F)
            {
                return a.F < b.F ? -1 : 1;
            }
            return a.I.CompareTo(b.I);
        }
    }
}

public sealed partial class Sim
{
    /// <summary>
    /// The pathfinder: A* over the eight neighbours priced by the pawn's own per-cell cost
    /// (<see cref="StepCost"/>), a diagonal step allowed only when both cardinal cells beside it are walkable.
    /// It returns the cells to enter after the pawn's position, valid until the next call, or null when there
    /// is no path. Ties break by cell index.
    /// </summary>
    internal List<int>? FindPath(SimPawn p, Cell to)
    {
        var g = G;
        var sr = _search;
        sr.Reset(g.W * g.H);
        var start = g.Idx(p.Pos);
        var goal = g.Idx(to);

        // Cheapest possible cardinal and diagonal step, for an admissible heuristic.
        var (card, diag) = CheapestTicks(p.Speed);
        double H(Cell c)
        {
            double dx = Grid.Abs32(c.X - to.X), dz = Grid.Abs32(c.Z - to.Z);
            return (card * (Math.Max(dx, dz) - Math.Min(dx, dz))) + (diag * Math.Min(dx, dz));
        }

        sr.Seen[start] = sr.Gen;
        sr.GScore[start] = 0;
        sr.From[start] = -1;
        sr.Open.Enqueue(start, (H(p.Pos), start));
        while (sr.Open.TryDequeue(out var ci, out _))
        {
            if (sr.Closed[ci] == sr.Gen)
            {
                continue;
            }
            sr.Closed[ci] = sr.Gen;
            if (ci == goal)
            {
                return sr.Trace(goal);
            }
            var cc = g.CellAt(ci);
            for (var k = 0; k < Grid.Adjacent.Length; k++)
            {
                var nb = Grid.Add(cc, Grid.Adjacent[k]);
                if (!g.Walkable(nb))
                {
                    continue;
                }
                if (k >= 4 && (!g.Walkable(new Cell(nb.X, cc.Z)) || !g.Walkable(new Cell(cc.X, nb.Z))))
                {
                    continue;
                }
                var ni = g.Idx(nb);
                if (sr.Closed[ni] == sr.Gen)
                {
                    continue;
                }
                var gs = sr.GScore[ci] + StepCost(p.Speed, cc, nb) + PathPenalty(p, ni, to);
                if (sr.Seen[ni] == sr.Gen && gs >= sr.GScore[ni])
                {
                    continue;
                }
                sr.Seen[ni] = sr.Gen;
                sr.GScore[ni] = gs;
                sr.From[ni] = ci;
                sr.Open.Enqueue(ni, (gs + H(nb), ni));
            }
        }
        return null;
    }

    /// <summary>The lowest cardinal and diagonal ticksPerMove over roofed and unroofed cells, so the heuristic never overestimates.</summary>
    private (double Card, double Diag) CheapestTicks(double speed)
    {
        if (speed == 0)
        {
            return (MaxTicksPerMove, MaxTicksPerMove);
        }
        var t = 60 / speed;
        t = Math.Min(t, t / Weather);
        static double Clamp(double v) => Math.Min(Math.Max(v, 1), MaxTicksPerMove);
        return (Clamp(t), Clamp(t * DiagonalFactor));
    }
}
