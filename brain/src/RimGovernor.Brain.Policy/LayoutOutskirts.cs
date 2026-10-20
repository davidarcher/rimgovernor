namespace RimGovernor.Brain.Policy;

/// <summary>The outskirts rooms (tomb and morgue) as station-less room nodes of the station graph.</summary>
public static partial class LayoutOutskirtsRules
{
    /// <summary>Why the graph holds an outskirts room: the burial demand asks for it, or the plan already holds it.</summary>
    public const string ReasonOutskirtsRoom = "outskirts_room_owed";
}

partial record LayoutPlan
{
    /// <summary>
    /// The tombs and morgues the burial demand asks for (RoomDemand.Tomb, RoomDemand.Morgue) or the plan holds, as
    /// graph nodes of the room size table's size. A plan with no dead owes neither, so a first plan holds none; a
    /// held one stays a node, so a standing tomb or morgue is never retired.
    /// </summary>
    public static IReadOnlyList<RoomNode> OutskirtsRoomNodes(LayoutPlan plan, RoomDemand demand)
    {
        var nodes = new List<RoomNode>();
        (PlannedRole Role, int Wanted, bool Cold)[] roles =
        [
            (LayoutSurveyRules.PlannedTomb, demand.Tomb, false),
            (LayoutSurveyRules.PlannedMorgue, demand.Morgue, true),
        ];
        foreach (var (role, asked, cold) in roles)
        {
            var size = LayoutTripsRules.RoomSize(role);
            var wanted = Math.Max(asked, plan.RoomsOf(role).Count);
            for (var n = 1; n <= wanted; n++)
            {
                nodes.Add(new RoomNode { Id = n == 1 ? Wire.Name(role) : $"{Wire.Name(role)}-{n}", Role = role, Area = size[0] * size[1], Cold = cold, Reason = LayoutOutskirtsRules.ReasonOutskirtsRoom });
            }
        }
        return nodes;
    }
}
