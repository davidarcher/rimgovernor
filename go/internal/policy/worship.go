package policy

// The worship room (#1658, epic #1653). An ideoligion that requires buildings
// (Ideoligion.RequiredBuildings: the building precepts' ThingDefs and the
// held rituals' required buildings, all read from the game) is owed one
// worship room holding one of each. It is staged exactly like the child
// rooms (child_rooms.go, layout_child_rooms.go): the layout review grows a
// core room sized to the footprints, MaintainHousing shells it and places the
// buildings at the shared template's slots. Names come from the ideoligion
// and footprints from the native catalog, never constants here. The ideology
// read carries no room-quality requirement, so none is staged.

// ModuleWorship is the worship room's plan role.
const ModuleWorship ModuleRole = "worship"

// WorshipRoomNeed is the worship room the ideoligion owes, false when it
// requires no building.
func WorshipRoomNeed(ideo Ideoligion) (ChildRoomNeed, bool) {
	required := ideo.RequiredBuildings()
	if len(required) == 0 {
		return ChildRoomNeed{}, false
	}
	need := ChildRoomNeed{Role: RoomRoleWorshipRoom, Module: ModuleWorship}
	for _, name := range required {
		need.Furniture = append(need.Furniture, ChildFurniture{Defs: []string{name}, Count: 1})
	}
	return need, true
}
