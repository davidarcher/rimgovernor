namespace RimGovernor.Brain.Policy;

/// <summary>One roofed-set decision for the caller to journal. Room is the room's id (0 for a decision about the whole set). Cells are the cells a reason names.</summary>
public sealed record RoofDecision
{
    public int Room { get; init; }
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<Cell> Cells { get; init; } = [];
}

/// <summary>What the roofed set reads beside the plan. Curve is the seasonal outdoor temperature curve (twelve means, C); Cold and Hot are the survey's latches. RoofDistance is the game's roof support distance; zero is unknown.</summary>
public sealed record RoofInput
{
    public Fact<EquatableArray<double>> Curve { get; init; }
    public bool Cold { get; init; }
    public bool Hot { get; init; }
    public double RoofDistance { get; init; }
}

/// <summary>The roofed cells and the decisions behind them.</summary>
public sealed record RoofResult
{
    public Region Roofed { get; init; } = new();
    public IReadOnlyList<RoofDecision> Decisions { get; init; } = [];
}

public sealed partial record LayoutPlan
{
    /// <summary>Whether a roofed cell lies inside an outdoor room (Valid rejects it).</summary>
    private bool RoofedOutdoors()
    {
        if (Roofed.Empty())
        {
            return false;
        }
        return Rooms.Any(r => r.Role.IsOutdoor() && Roofed.Overlaps(r.Region));
    }
}

/// <summary>Ports <c>layout_roof.go</c>: on a map whose outdoor temperature is not good for pawns, every room except the outdoor roles is roofed.</summary>
public static partial class PolicyFunctions
{
    public const string ReasonRoofTemperate = "roof_temperate";
    public const string ReasonRoofRoom = "roof_room";
    public const string ReasonRoofUnsupported = "roof_unsupported";
    public const string ReasonRoofSupportless = "roof_support_unknown";

    /// <summary>The temperature range pawns are comfortable in: the temperature planner's cold and hot limits.</summary>
    public static Band ComfortBand()
    {
        var p = DefaultRoundsPolicy();
        return new Band { MinC = p.ColdEnter, MaxC = p.HotEnter };
    }

    /// <summary>Whether the map's outdoor temperature is not good for pawns: a latch set, or the curve cold or hot.</summary>
    public static bool BadClimate(RoofInput input)
    {
        var known = input.Curve.TryGetValue(out var curve);
        return input.Cold || input.Hot || (known && (ColdMapCurve(curve!) || HotMapCurve(curve!)));
    }

    /// <summary>The roofed set of a plan: empty on a temperate map; else the floor of every room that is not an outdoor role. It is a pure function of the plan and the input.</summary>
    public static RoofResult PlanRoofed(LayoutPlan p, RoofInput input)
    {
        var decisions = new List<RoofDecision>();
        if (!BadClimate(input))
        {
            decisions.Add(new RoofDecision { Verdict = TilePlace, Reason = ReasonRoofTemperate });
            return new RoofResult { Decisions = decisions };
        }
        var cells = new List<Cell>();
        var outdoor = new HashSet<Cell>();
        foreach (var r in p.Rooms)
        {
            if (r.Role.IsOutdoor())
            {
                outdoor.UnionWith(r.Region.Cells());
                continue;
            }
            cells.AddRange(r.Region.Cells());
            decisions.Add(new RoofDecision { Room = r.Id, Verdict = TilePlace, Reason = ReasonRoofRoom });
        }

        // an outdoor room holds no roof
        var roofed = RegionOf([.. cells.Where(c => !outdoor.Contains(c))]);
        if (input.RoofDistance <= 0)
        {
            decisions.Add(new RoofDecision { Verdict = TileUnsatisfied, Reason = ReasonRoofSupportless });
            return new RoofResult { Roofed = roofed, Decisions = decisions };
        }
        var far = UnsupportedRoof(p, roofed, input.RoofDistance);
        if (far.Count > 0)
        {
            decisions.Add(new RoofDecision { Verdict = TileUnsatisfied, Reason = ReasonRoofUnsupported, Cells = far });
        }
        return new RoofResult { Roofed = roofed, Decisions = decisions };
    }

    /// <summary>The roofed cells farther than radius from every wall cell of a room that holds a roof, in cell order.</summary>
    private static List<Cell> UnsupportedRoof(LayoutPlan p, Region roofed, double radius)
    {
        var walls = new List<Cell>();
        foreach (var r in p.Rooms)
        {
            if (!r.Role.IsOutdoor() && !r.Unfenced())
            {
                walls.AddRange(r.Region.Walls().Cells());
            }
        }
        var far = new List<Cell>();
        foreach (var c in roofed.Cells())
        {
            var near = walls.Any(w => double.Hypot((double)c.X - w.X, (double)c.Z - w.Z) <= radius);
            if (!near)
            {
                far.Add(c);
            }
        }
        far.Sort(CellCompare);
        return far;
    }

    private static int CellCompare(Cell a, Cell b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0;
}
