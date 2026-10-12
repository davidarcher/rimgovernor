package policy

// The gear rooms: an armory for weapons and armor and a
// wardrobe for clothing are core rooms layout adds only when the storage
// planner signals that stored gear of the kind outgrew its zone
// (RoomDemand); with no demand neither is planned. The armory stands beside
// the storage room, the wardrobe beside the workshop, which hosts every bench
// including the tailor's (besideRoles). A room already in the plan answers
// its demand for good: rooms are only retired by the reconcile steps in
// layout_retire.go and none moves or resizes, so a zone that stays
// full after the room stands asks for nothing more.

const (
	PlannedArmory   PlannedRole = "armory"
	PlannedWardrobe PlannedRole = "wardrobe"
)

// gearRooms are the gear rooms in the order they are added.
var gearRooms = []PlannedRole{PlannedArmory, PlannedWardrobe}

// ReasonGearRoom is why the graph holds a gear room: the stored gear of its kind
// outgrew its zone (RoomDemand) or the plan already holds the room (#3003).
const ReasonGearRoom = "gear_room_demanded"

// GearRoomNodes are the gear rooms (armory, wardrobe) the plan holds or the
// demand asks for as graph nodes, each of the old siting's size. The armory
// stands beside the storage room and the wardrobe beside the workshop (the
// neighbour rules of layout_adjacency.go); those are station groups, so the
// nodes carry no required link, and the rooms are priced by the walk of the
// flows that reach them (RoomNode.Flows) like any room node.
func GearRoomNodes(plan LayoutPlan, demand RoomDemand) []RoomNode {
	wanted := map[PlannedRole]bool{PlannedArmory: demand.Armory, PlannedWardrobe: demand.Wardrobe}
	var nodes []RoomNode
	for _, role := range gearRooms {
		if size := coreRoomSize[role]; wanted[role] || len(plan.roomsOf(role)) > 0 {
			nodes = append(nodes, RoomNode{ID: string(role), Role: role, Area: int(size[0] * size[1]), Reason: ReasonGearRoom})
		}
	}
	return nodes
}

// GearRoomsOwed is the gear rooms demand asks for that plan lacks.
func GearRoomsOwed(plan LayoutPlan, demand RoomDemand) []PlannedRole {
	wanted := map[PlannedRole]bool{PlannedArmory: demand.Armory, PlannedWardrobe: demand.Wardrobe}
	have := map[PlannedRole]bool{}
	for _, r := range plan.AllRooms() {
		have[r.Role] = true
	}
	var owed []PlannedRole
	for _, role := range gearRooms {
		if wanted[role] && !have[role] {
			owed = append(owed, role)
		}
	}
	return owed
}
