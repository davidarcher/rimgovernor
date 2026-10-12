package policy

// The outskirts rooms as station-less room nodes of the station graph (#2882).
// The tomb, morgue, graveyard, waste yard and incinerator are rooms the plan
// owes from its start (OutskirtsOwed) that hold no catalog station for the
// graph to size: the node asks the floor the room's slot gives
// (OutskirtsSlots) and the room keeps its role's template (the tomb's
// sarcophagus, the graveyard's graves, the incinerator itself), which the
// interior placer sets. No distance band sits on the nodes (#2987): no minimum
// clear ground and no maximum reach, since once walls go up a corpse lying in
// the cluster is out of every indoor pawn's sight anyway. The walk of the flows
// that reach a room (RoomNode.Flows) is the only pressure on where it stands.
// The repo has no corpse or waste volume model, so these rooms carry no flow
// yet and are unpriced by distance (their ground is chosen by the plan's cell
// terms alone), never forced near or far. One constraint sits on the nodes:
//
//   - Cold. The morgue is kept cold: clustering gives it the cooling station
//     any cold group carries (the catalog's most efficient cooler, its
//     footprint and a clearance cell), as for a freezer.
//
// The incinerator also stands beside the waste yard, a required link: the dump
// hauls its burnable waste into the incinerator (incinerationOwner). The rooms
// of the old cluster stand in fixed slots; this is the station planner's input
// for the same rooms, and the old planner stays authoritative.

// ReasonOutskirtsRoom is why the graph holds an outskirts room: the plan owes
// each from its start.
const ReasonOutskirtsRoom = "outskirts_room_owed_from_start"

// OutskirtsRoomNodes are the five outskirts rooms as graph nodes.
func OutskirtsRoomNodes() []RoomNode {
	tomb, morgue := coreRoomSize[PlannedTomb], coreRoomSize[PlannedMorgue]
	inc := IncineratorOutline - 2
	node := func(role PlannedRole, w, h, width int32) RoomNode {
		return RoomNode{ID: string(role), Role: role, Area: int(w * h), Width: width, Reason: ReasonOutskirtsRoom}
	}
	nodes := []RoomNode{
		node(PlannedTomb, tomb[0], tomb[1], 0),
		node(PlannedMorgue, morgue[0], morgue[1], 0),
		node(PlannedGraveyard, GraveyardW, GraveyardH, GraveyardH),
		node(PlannedWasteYard, WasteYardW, WasteYardH, WasteYardH),
		node(PlannedIncinerator, inc, inc, 0),
	}
	nodes[1].Cold = true
	nodes[4].Beside = string(PlannedWasteYard)
	return nodes
}
