namespace RimGovernor.Brain.Policy;

/// <summary>Ports <c>layout_outskirts.go</c> and <c>layout_outskirts_nodes.go</c>: the outskirts rooms as station-less room nodes of the station graph.</summary>
public static partial class PolicyFunctions
{
    /// <summary>The graveyard's interior.</summary>
    public const int GraveyardW = 11;

    public const int GraveyardH = 7;

    /// <summary>The waste yard's interior: 77 cells, the incinerator room takes a corner, the dump the rest.</summary>
    public const int WasteYardW = 11;

    public const int WasteYardH = 7;

    /// <summary>The incinerator room's walled outline side: a 3x3 interior and its ring.</summary>
    public const int IncineratorOutline = 5;

    /// <summary>The waste yard's plan role: an Outdoor room that holds the dump zone's ground and the incinerator room.</summary>
    public static PlannedRole PlannedWasteYard => PlannedRole.WasteYard;

    /// <summary>Why the graph holds an outskirts room: the plan owes each from its start.</summary>
    public const string ReasonOutskirtsRoom = "outskirts_room_owed_from_start";

    /// <summary>Why the graph holds a graveyard beyond the first: the graveyards in use are full or the plan already holds them.</summary>
    public const string ReasonGraveyardDemand = "graveyard_demanded";

    /// <summary>The graveyards beyond the first of the outskirts, each of the first one's size; wanted is the graveyards in all.</summary>
    public static List<RoomNode> GraveyardRoomNodes(int wanted)
    {
        var nodes = new List<RoomNode>();
        for (var n = 2; n <= wanted; n++)
        {
            nodes.Add(new RoomNode { Id = $"{PlannedGraveyard}-{n}", Role = PlannedGraveyard, Area = GraveyardW * GraveyardH, Width = GraveyardH, Reason = ReasonGraveyardDemand });
        }
        return nodes;
    }

    /// <summary>The five outskirts rooms as graph nodes.</summary>
    public static List<RoomNode> OutskirtsRoomNodes()
    {
        var tomb = RoomSize(PlannedTomb);
        var morgue = RoomSize(PlannedMorgue);
        const int inc = IncineratorOutline - 2;
        static RoomNode Node(PlannedRole role, int w, int h, int width) => new() { Id = Wire.Name(role), Role = role, Area = w * h, Width = width, Reason = ReasonOutskirtsRoom };
        var nodes = new List<RoomNode>
        {
            Node(PlannedTomb, tomb[0], tomb[1], 0),
            Node(PlannedMorgue, morgue[0], morgue[1], 0),
            Node(PlannedGraveyard, GraveyardW, GraveyardH, GraveyardH),
            Node(PlannedWasteYard, WasteYardW, WasteYardH, WasteYardH),
            Node(PlannedRole.Incinerator, inc, inc, 0),
        };
        nodes[1] = nodes[1] with { Cold = true };
        nodes[4] = nodes[4] with { Beside = Wire.Name(PlannedWasteYard) };
        return nodes;
    }
}

public sealed partial record LayoutPlan
{
    /// <summary>The plan's waste yards.</summary>
    public List<PlannedRoom> WasteYardRooms() => RoomsOf(PlannedWasteYard);
}
