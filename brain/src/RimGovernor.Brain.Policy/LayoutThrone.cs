namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The plan's first throne room whose interior holds at least area cells (the room a title's area requirement is met in); Ok is false when none.</summary>
    public (PlannedRoom Room, bool Ok) ThroneRoomFor(int area)
    {
        foreach (var r in Rooms)
        {
            if (r.Role == PlannedRole.Throne && (long)r.Region.Bounds().Width * r.Region.Bounds().Height >= area)
            {
                return (r, true);
            }
        }
        return (new PlannedRoom(), false);
    }
}

/// <summary>Ports <c>layout_throne.go</c>: the throne room, one core room sized from the next title's minimum area.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The throne room's plan role.</summary>
    public static PlannedRole PlannedThrone => PlannedRole.Throne;

    /// <summary>The shortest throne room side.</summary>
    private const int ThroneMinSide = 4;

    /// <summary>
    /// The throne room interiors (width along the spine, depth away from it) holding at least area cells, best first:
    /// the fewest cells, then the aspect nearest two thirds (wider than deep). Depth stays within the core's
    /// cross-section (CoreMaxDepth) and no room is more than half again the area. A crowded plan takes the next shape
    /// when the best finds no slot. The area is the interior's cell count, walls excluded.
    /// </summary>
    public static List<(int W, int D)> ThroneRoomSizes(int area)
    {
        var output = new List<(int W, int D)>();
        var limit = Math.Max(area * 3 / 2, ThroneMinSide * ThroneMinSide);
        for (var d = ThroneMinSide; d <= CoreMaxDepth; d++)
        {
            var w = Math.Max((area + d - 1) / d, ThroneMinSide);
            if (w * d <= limit)
            {
                output.Add((w, d));
            }
        }
        if (output.Count == 0)
        {
            output.Add(((area + CoreMaxDepth - 1) / CoreMaxDepth, CoreMaxDepth));
        }
        return [.. output.OrderBy(s => s.W * s.D).ThenBy(s => Abs32((2 * s.W) - (3 * s.D)))];
    }
}
