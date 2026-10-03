package policy

// The gear rooms (#1773, epic #1765): an armory for weapons and armor and a
// wardrobe for clothing are core rooms layout adds only when the storage
// planner signals that stored gear of the kind outgrew its zone
// (GearRoomDemand); with no demand neither is planned. The armory stands beside
// the barracks, the wardrobe beside the workshop, which hosts every bench
// including the tailor's (besideRoles). A room already in the plan answers
// its demand for good: rooms are only retired by the reconcile steps in
// layout_retire.go and none moves or resizes, so a zone that stays
// full after the room stands asks for nothing more.

const (
	ModuleArmory   ModuleRole = "armory"
	ModuleWardrobe ModuleRole = "wardrobe"
)

// gearRooms are the gear rooms in the order they are added.
var gearRooms = []ModuleRole{ModuleArmory, ModuleWardrobe}

// GearRoomsOwed is the gear rooms demand asks for that plan lacks.
func GearRoomsOwed(plan LayoutPlan, demand GearRoomDemand) []ModuleRole {
	wanted := map[ModuleRole]bool{ModuleArmory: demand.Armory, ModuleWardrobe: demand.Wardrobe}
	have := map[ModuleRole]bool{}
	for _, r := range plan.AllRooms() {
		have[r.Role] = true
	}
	var owed []ModuleRole
	for _, role := range gearRooms {
		if wanted[role] && !have[role] {
			owed = append(owed, role)
		}
	}
	return owed
}

// growGearRooms adds each gear room demand asks for and plan lacks, at the
// slot beside its anchor room, else the nearest core slot like any other
// core room. It reports whether a room was added.
func growGearRooms(plan LayoutPlan, demand GearRoomDemand) (LayoutPlan, bool) {
	if len(plan.Spine) == 0 {
		return plan, false
	}
	grew := false
	for _, role := range GearRoomsOwed(plan, demand) {
		var added bool
		if plan, added = growModuleRoom(plan, role, [][2]int32{coreRoomSize[role]}); added {
			grew = true
		}
	}
	return plan, grew
}
