package policy

import "errors"

// The gear rooms (#1773, epic #1765): an armory for weapons and armor and a
// wardrobe for clothing are core rooms layout adds only when the storage
// planner signals that stored gear of the kind outgrew its zone
// (RoomDemand); with no demand neither is planned. The armory stands beside
// the storage room, the wardrobe beside the workshop, which hosts every bench
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
func GearRoomsOwed(plan LayoutPlan, demand RoomDemand) []ModuleRole {
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
// core room. It reports whether a room was added and the rooms it could not
// place.
func growGearRooms(plan LayoutPlan, demand RoomDemand, sc *planScorer) (LayoutPlan, bool, error) {
	if len(plan.Hallways()) == 0 {
		return plan, false, nil
	}
	grew := false
	var unplaced []error
	for _, role := range GearRoomsOwed(plan, demand) {
		var added bool
		var err error
		if plan, added, err = SiteRoom(plan, sc, role, coreRoomSize[role]); added {
			grew = true
		} else if err != nil {
			unplaced = append(unplaced, err)
		}
	}
	return plan, grew, errors.Join(unplaced...)
}
