namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>The materials yard an outline holds: an Outdoor room on the outline less its one-cell margin.</summary>
    internal static PlannedRoom YardRoom(Rectangle outline) =>
        new() { Role = LayoutYardRules.PlannedYard, Region = RegionRules.RectRegion(LayoutPerimeterRules.Pad(outline, -1)), Outdoor = true };
}

/// <summary>The materials yard, a planned Outdoor room beside the core.</summary>
public static partial class LayoutYardRules
{
    /// <summary>The plan role of a yard viewed as an Outdoor room.</summary>
    public static PlannedRole PlannedYard => PlannedRole.Yard;

    /// <summary>The yard's interior width (117 cells with the height).</summary>
    public const int YardW = 13;

    public const int YardH = 9;

    /// <summary>How many yards the plan should hold: one from the start, more when demand asks for them.</summary>
    public static int YardRoomsWanted(RoomDemand demand) => Math.Max(demand.Yard, 1);

    /// <summary>Why the graph holds a yard: one from the start, more when the demand asks, or the plan already holds them.</summary>
    public const string ReasonYardRoom = "yard_room_owed";
}

partial record LayoutPlan
{
    /// <summary>The yards the plan holds or the demand asks for as graph nodes, each of the one size; the tiler sites them in the base.</summary>
    public static IReadOnlyList<RoomNode> YardRoomNodes(LayoutPlan plan, RoomDemand demand)
    {
        var wanted = Math.Max(LayoutYardRules.YardRoomsWanted(demand), plan.RoomsOf(LayoutYardRules.PlannedYard).Count);
        var nodes = new List<RoomNode>();
        for (var n = 1; n <= wanted; n++)
        {
            nodes.Add(new RoomNode { Id = n == 1 ? Wire.Name(LayoutYardRules.PlannedYard) : $"{Wire.Name(LayoutYardRules.PlannedYard)}-{n}", Role = LayoutYardRules.PlannedYard, Area = LayoutYardRules.YardW * LayoutYardRules.YardH, Width = LayoutYardRules.YardH, Reason = LayoutYardRules.ReasonYardRoom });
        }
        return nodes;
    }
}
