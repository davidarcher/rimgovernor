package policy

// The day-1 shelter and the throne room as station-less room nodes of the
// station graph (#2958). Both are special rooms the station chain keeps: the
// graph asks their floor, the embedding sites them and the tiler gives them
// their walls, door and (on a bad-climate map) the airlock decision, as for
// any room. They hold no catalog station for the graph to size, so the node
// asks the floor the old siting sized them to and the room keeps its role's
// template (interior_shelter.go, interior_throne.go), which the interior placer
// sets. The tiler places them (layout_tile.go).
//
//   - Shelter. One temporary room, ShelterInteriorArea cells for the colonists
//     (a bunk each, campfires on a cold map, a cooler on a hot one, the crafting
//     spot and research table). It has no distance band (#2987): the walk of its
//     meal trips to the table (RoomNode.Flows, shelterMeals) is the only pressure
//     on where it stands; with no table or no meals read it is
//     unpriced. The old siting's battery porch is not planned.
//   - Throne. One room of the title's area, sited like any other room.
//
// Unknown stays unknown: a shelter needs the colony's size, a throne room the
// area owed; a fact not read asks for no such room and is never read as zero.

const (
	// ReasonShelterRoom is why the graph holds the shelter: a fresh plan owes
	// the colonists a roof from day one.
	ReasonShelterRoom = "shelter_day_one_room"
	// ReasonThroneRoom is why the graph holds a throne room: a title asks one.
	ReasonThroneRoom = "throne_room_owed"
)

// SeedRooms are the special rooms a plan is asked to hold beyond its stations.
type SeedRooms struct {
	// Shelter asks for the day-1 shelter for Colonists on a Cold or Hot map.
	Shelter   bool
	Colonists int
	Cold, Hot bool
	// ThroneArea is the interior cells of the throne room asked, 0 asks for none.
	ThroneArea int
}

// SeedRoomNodes are the shelter and throne nodes for s.
func SeedRoomNodes(s SeedRooms) []RoomNode {
	var nodes []RoomNode
	if s.Shelter {
		area := ShelterInteriorArea(s.Colonists, ShelterCampfires(s.Cold), ShelterCoolers(s.Hot))
		nodes = append(nodes, RoomNode{ID: string(PlannedShelter), Role: PlannedShelter, Area: area, Reason: ReasonShelterRoom})
	}
	if s.ThroneArea > 0 {
		nodes = append(nodes, RoomNode{ID: string(PlannedThrone), Role: PlannedThrone, Area: s.ThroneArea, Reason: ReasonThroneRoom})
	}
	return nodes
}
