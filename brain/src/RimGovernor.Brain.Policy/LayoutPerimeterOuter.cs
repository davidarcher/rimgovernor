namespace RimGovernor.Brain.Policy;

/// <summary>A gate on a run shorter than the pitch: its outer-face cell, the way out from it and the way along the wall.</summary>
internal readonly record struct StepGate(Rectangle Area, Cell Face, Cell Out, Cell Along)
{
    /// <summary>
    /// Whether a gate leads outside: the strip of three cells wide and three deep straight out from its door holds no
    /// wall and no rock. On a step of a squared-off diagonal the next step's wall stands in that strip, and the door
    /// would open into the jog.
    /// </summary>
    public bool OpensOut(Func<Cell, bool> blocked)
    {
        for (var k = 1; k <= 3; k++)
        {
            for (var a = -1; a <= 1; a++)
            {
                if (blocked(new Cell(Face.X + (Out.X * k) + (Along.X * a), Face.Z + (Out.Z * k) + (Along.Z * a))))
                {
                    return false;
                }
            }
        }
        return true;
    }
}

/// <summary>Ports <c>layout_perimeter_outer.go</c>: the core ring also walls in the geothermal enclosures that crowd it; the outer ring that once walled the rest apart was removed.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The free cells between the core ring (and the killbox approach) and the outer ring.</summary>
    internal const int PerimeterOuterGap = 1;

    /// <summary>The margin kept between the outer ring and what it encloses.</summary>
    internal const int PerimeterOuterYard = 1;

    /// <summary>The reservation kinds the outer ring walls in whole: the geothermal enclosures.</summary>
    private static bool OuterEnclosed(ReservationKind k) => k == ReserveGeothermal;

    /// <summary>The reservations the core ring walls in with the rooms: the materials yard, the barns and their pens stand beside the core, inside its wall, not out in the fields. The turbines with their wind lanes are taken in the same way (CoreBaseFootprint).</summary>
    private static bool InnerEnclosed(ReservationKind k) => k == ReserveYard || k == ReservationKind.Barn || k == ReservationKind.Pen;

    /// <summary>
    /// Walls side sd's open cells into walls, run by run between the positions skip reports (an opening, bare terrain,
    /// soft ground), and returns each run's gates on the PerimeterGatePitch: a run shorter than the pitch (a step of a
    /// squared-off diagonal) gives step gates, kept by PitchGates. Raiders neither mine nor attack natural rock: a run
    /// is walled only on its open cells, and not behind a rock row that spans the column and both neighbours, which no
    /// raider can reach round.
    /// </summary>
    internal static (List<Rectangle> Gates, List<StepGate> StepGates) WallRuns(RingSide sd, Func<Cell, bool> impassable, HashSet<Cell> walls, Func<int, bool> skip)
    {
        var gates = new List<Rectangle>();
        var stepGates = new List<StepGate>();
        bool Sealed(int p, int t)
        {
            for (var u = 0; u < t; u++)
            {
                if (impassable(sd.At(p - 1, u)) && impassable(sd.At(p, u)) && impassable(sd.At(p + 1, u)))
                {
                    return true;
                }
            }
            return false;
        }
        bool OpenColumn(int p)
        {
            for (var t = 0; t < PerimeterThick; t++)
            {
                if (impassable(sd.At(p, t)))
                {
                    return false;
                }
            }
            return true;
        }
        var start = -1;
        void Flush(int end)
        {
            if (start < 0)
            {
                return;
            }
            for (var p = start; p <= end; p++)
            {
                for (var t = 0; t < PerimeterThick; t++)
                {
                    var c = sd.At(p, t);
                    if (!impassable(c) && !Sealed(p, t))
                    {
                        walls.Add(c);
                    }
                }
            }
            var n = end - start + 1;
            if (n >= 5)
            {
                var first = start + Math.Min(PerimeterGatePitch / 2, n / 2);
                for (var p = first; p <= end; p += PerimeterGatePitch)
                {
                    if (!OpenColumn(p))
                    {
                        continue;
                    }
                    var g = RectOf(sd.Base(p), sd.At(p, PerimeterThick - 1));
                    if (n < PerimeterGatePitch)
                    {
                        stepGates.Add(new StepGate(g, sd.Base(p), new Cell(-sd.Inward.X, -sd.Inward.Z), sd.Along));
                    }
                    else
                    {
                        gates.Add(g);
                    }
                }
            }
            start = -1;
        }
        for (var p = sd.Lo; p <= sd.Hi; p++)
        {
            if (skip(p))
            {
                Flush(p - 1);
            }
            else if (start < 0)
            {
                start = p;
            }
        }
        Flush(sd.Hi);
        return (gates, stepGates);
    }

    /// <summary>Adds to gates the step gates a pitch clear of every other that lead outside, so a staircase is gated about as often as a straight side and never into a jog.</summary>
    internal static List<Rectangle> PitchGates(List<Rectangle> gates, List<StepGate> stepGates, Func<Cell, bool> blocked)
    {
        foreach (var step in stepGates)
        {
            var g = step.Area;
            var clear = step.OpensOut(blocked);
            foreach (var o in gates)
            {
                clear = clear && Math.Max(Math.Max(o.X - g.X, g.X - o.X), Math.Max(o.Z - g.Z, g.Z - o.Z)) >= PerimeterGatePitch;
            }
            if (clear)
            {
                gates.Add(g);
            }
        }
        return gates;
    }

    /// <summary>
    /// Marks the cells of a w x h map the core ring walls in: each room's interior with its walls, the pen, barn, vet
    /// room and turbine pairs beside them, and any geothermal crowding that footprint. It follows the plan's real
    /// outline, not its bounding rectangle.
    /// </summary>
    internal static bool[] CoreFootprint(LayoutPlan plan, int w, int h)
    {
        var fp = CoreBaseFootprint(plan, w, h);
        const int gap = PerimeterGap;
        foreach (var r in plan.Reservations)
        {
            if (OuterEnclosed(r.Kind) && CrowdsCore(fp, w, h, r.Area, gap))
            {
                MarkRegion(fp, w, h, r.Area.Grow(1));
            }
        }
        return fp;
    }

    /// <summary>CoreFootprint before any outer-ring unit is taken in.</summary>
    internal static bool[] CoreBaseFootprint(LayoutPlan plan, int w, int h)
    {
        var fp = new bool[Math.Max(w, 0) * Math.Max(h, 0)];
        foreach (var r in plan.Rooms)
        {
            MarkRect(fp, w, h, Pad(r.Region.Bounds(), 1));
        }
        foreach (var r in plan.Reservations)
        {
            if (InnerEnclosed(r.Kind))
            {
                MarkRegion(fp, w, h, r.Area.Grow(1));
            }
        }
        foreach (var s in plan.LiveSites(NodeTurbine))
        {
            MarkRegion(fp, w, h, s.Footprint.Grow(1));
        }
        return fp;
    }

    /// <summary>Sets the cells of r that lie on the w x h map.</summary>
    internal static void MarkRect(bool[] fp, int w, int h, Rectangle r)
    {
        for (var z = Math.Max(r.Z, 0); z < Math.Min(r.Z + r.Height, h); z++)
        {
            for (var x = Math.Max(r.X, 0); x < Math.Min(r.X + r.Width, w); x++)
            {
                fp[(z * w) + x] = true;
            }
        }
    }

    /// <summary>Sets the cells of g that lie on the w x h map: a ring marks its ring, not its bounding rectangle.</summary>
    internal static void MarkRegion(bool[] fp, int w, int h, Region g)
    {
        foreach (var run in g.Runs)
        {
            if (run.Z < 0 || run.Z >= h)
            {
                continue;
            }
            for (var x = Math.Max(run.X, 0); x < Math.Min(run.X + run.Length, w); x++)
            {
                fp[(run.Z * w) + x] = true;
            }
        }
    }

    /// <summary>
    /// Traces the core ring's enclosure: the core grown by the killbox yard, plus each outer-ring unit it takes in
    /// (CrowdsCore) grown only by PerimeterOuterYard, as a blob of its own: a geothermal has no killbox lane to clear,
    /// so it stands hard against the wall.
    /// </summary>
    internal static Enclosure CoreEnclosure(LayoutPlan plan, int w, int h)
    {
        var baseFp = CoreBaseFootprint(plan, w, h);
        const int gap = PerimeterGap;
        var tight = new bool[baseFp.Length];
        foreach (var r in plan.Reservations)
        {
            if (OuterEnclosed(r.Kind) && CrowdsCore(baseFp, w, h, r.Area, gap))
            {
                MarkRegion(tight, w, h, r.Area);
            }
        }
        const int m = LayoutEdgeMargin + PerimeterThick;
        var yard = new Rectangle { X = m, Z = m, Width = w - (2 * m), Height = h - (2 * m) };
        var grown = GrowRegion(baseFp, w, h, gap);
        var near = GrowRegion(tight, w, h, PerimeterOuterYard);
        for (var i = 0; i < near.Length; i++)
        {
            grown[i] = grown[i] || near[i];
        }
        return NewEnclosure(CloseRegion(grown, w, h, yard), w, h);
    }

    /// <summary>How far the outer ring needs a unit to stand off the core footprint: the core ring's yard, its thickness, the gap and the outer ring's thickness.</summary>
    internal const int OuterClear = PerimeterGap + PerimeterThick + PerimeterOuterGap + PerimeterThick;

    /// <summary>
    /// Whether area stands within OuterClear (Chebyshev), widened by the yard (gap) a herd grew past PerimeterGap, of the
    /// core footprint. The outer ring cannot wall such a unit whole, so the core ring takes it in.
    /// </summary>
    internal static bool CrowdsCore(bool[] fp, int w, int h, Region area, int gap)
    {
        var clear = OuterClear + gap - PerimeterGap;
        foreach (var run in area.Grow(clear).Runs)
        {
            if (run.Z < 0 || run.Z >= h)
            {
                continue;
            }
            for (var x = Math.Max(run.X, 0); x < Math.Min(run.X + run.Length, w); x++)
            {
                if (fp[(run.Z * w) + x])
                {
                    return true;
                }
            }
        }
        return false;
    }
}
