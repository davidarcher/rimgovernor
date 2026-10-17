namespace RimGovernor.Brain.Policy;

/// <summary>Ports <c>layout_outskirts.go</c> and <c>layout_outskirts_nodes.go</c>: the outskirts rooms as station-less room nodes of the station graph.</summary>
public static partial class LayoutOutskirtsRules
{
    /// <summary>Why the graph holds an outskirts room: the plan owes each from its start.</summary>
    public const string ReasonOutskirtsRoom = "outskirts_room_owed_from_start";

    /// <summary>The two outskirts rooms (tomb and morgue) as graph nodes.</summary>
    public static List<RoomNode> OutskirtsRoomNodes()
    {
        var tomb = LayoutTripsRules.RoomSize(LayoutSurveyRules.PlannedTomb);
        var morgue = LayoutTripsRules.RoomSize(LayoutSurveyRules.PlannedMorgue);
        static RoomNode Node(PlannedRole role, int w, int h) => new() { Id = Wire.Name(role), Role = role, Area = w * h, Reason = ReasonOutskirtsRoom };
        return
        [
            Node(LayoutSurveyRules.PlannedTomb, tomb[0], tomb[1]),
            Node(LayoutSurveyRules.PlannedMorgue, morgue[0], morgue[1]) with { Cold = true },
        ];
    }
}
