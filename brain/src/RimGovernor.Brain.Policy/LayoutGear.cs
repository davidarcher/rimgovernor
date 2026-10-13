namespace RimGovernor.Brain.Policy;

// The gear rooms: an armory for weapons and armor and a wardrobe for clothing are core rooms layout adds only
// when the storage planner signals that stored gear of the kind outgrew its zone (RoomDemand); with no demand
// neither is planned. The armory stands beside the storage room, the wardrobe beside the workshop, which hosts
// every bench including the tailor's (besideRoles). A room already in the plan answers its demand for good:
// rooms are only retired by the tiled replan, and none moves or resizes, so a zone that stays full after the
// room stands asks for nothing more.
public static partial class PolicyConstants
{
    /// <summary>The gear rooms in the order they are added.</summary>
    internal static readonly IReadOnlyList<PlannedRole> GearRooms = [PlannedRole.Armory, PlannedRole.Wardrobe];

    /// <summary>Why the graph holds a gear room: the stored gear of its kind outgrew its zone (RoomDemand) or the plan already holds the room (#3003).</summary>
    public const string ReasonGearRoom = "gear_room_demanded";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The gear rooms (armory, wardrobe) the plan holds or the demand asks for as graph nodes, each of the old
    /// siting's size. The armory stands beside the storage room and the wardrobe beside the workshop (the
    /// neighbour rules of layout_adjacency.go); those are station groups, so the nodes carry no required link,
    /// and the rooms are priced by the walk of the flows that reach them (RoomNode.Flows) like any room node.
    /// </summary>
    public static IReadOnlyList<RoomNode> GearRoomNodes(LayoutPlan plan, RoomDemand demand)
    {
        var wanted = new Dictionary<PlannedRole, bool> { [PlannedRole.Armory] = demand.Armory, [PlannedRole.Wardrobe] = demand.Wardrobe };
        var nodes = new List<RoomNode>();
        foreach (var role in GearRooms)
        {
            var size = RoomSize(role);
            if (wanted[role] || plan.RoomsOf(role).Count > 0)
            {
                nodes.Add(new RoomNode { Id = Wire.Name(role), Role = role, Area = size.W * size.H, Reason = ReasonGearRoom });
            }
        }
        return nodes;
    }

    /// <summary>The gear rooms demand asks for that plan lacks.</summary>
    public static IReadOnlyList<PlannedRole> GearRoomsOwed(LayoutPlan plan, RoomDemand demand)
    {
        var wanted = new Dictionary<PlannedRole, bool> { [PlannedRole.Armory] = demand.Armory, [PlannedRole.Wardrobe] = demand.Wardrobe };
        var have = new HashSet<PlannedRole>(plan.Rooms.Select(r => r.Role));
        var owed = new List<PlannedRole>();
        foreach (var role in GearRooms)
        {
            if (wanted[role] && !have.Contains(role))
            {
                owed.Add(role);
            }
        }
        return owed;
    }
}
