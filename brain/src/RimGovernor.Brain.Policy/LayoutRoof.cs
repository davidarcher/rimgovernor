namespace RimGovernor.Brain.Policy;

/// <summary>What the roofed set reads beside the plan. Curve is the seasonal outdoor temperature curve (twelve means, C); Cold and Hot are the survey's latches.</summary>
public sealed record RoofInput
{
    public Fact<EquatableArray<double>> Curve { get; init; }
    public bool Cold { get; init; }
    public bool Hot { get; init; }
}

/// <summary>The roofed cells.</summary>
public sealed record RoofResult
{
    public Region Roofed { get; init; } = new();
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

/// <summary>On a map whose outdoor temperature is not good for pawns, every room except the outdoor roles is roofed.</summary>
public static partial class LayoutRoofRules
{
    /// <summary>The temperature range pawns are comfortable in: the temperature planner's cold and hot limits.</summary>
    public static Band ComfortBand()
    {
        var p = RoundsRules.DefaultRoundsPolicy();
        return new Band { MinC = p.ColdEnter, MaxC = p.HotEnter };
    }

    /// <summary>Whether the map's outdoor temperature is not good for pawns: a latch set, or the curve cold or hot.</summary>
    public static bool BadClimate(RoofInput input)
    {
        var known = input.Curve.TryGetValue(out var curve);
        return input.Cold || input.Hot || (known && (LayoutShelterRules.ColdMapCurve(curve!) || LayoutShelterRules.HotMapCurve(curve!)));
    }
}

partial record LayoutPlan
{
    /// <summary>The roofed set of a plan: empty on a temperate map; else the floor of every room that is not an outdoor role. It is a pure function of the plan and the input.</summary>
    public static RoofResult PlanRoofed(LayoutPlan p, RoofInput input)
    {
        if (!LayoutRoofRules.BadClimate(input))
        {
            return new RoofResult();
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
        }

        // an outdoor room holds no roof
        return new RoofResult { Roofed = RegionRules.RegionOf([.. cells.Where(c => !outdoor.Contains(c))]) };
    }
}
