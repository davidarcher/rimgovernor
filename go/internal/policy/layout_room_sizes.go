package policy

// RoomSizeTable is the interior (width, depth) of the rooms whose size the
// tiler does not derive from a station set: the outskirts' tomb and morgue
// (layout_outskirts_nodes.go), the gear rooms added on demand
// (layout_gear.go) and the battery room (layout_power_derive.go). Each is a
// judgment, kept in one registered table so the numbers have an owner and the
// defs the rooms are built to hold fail the dangling-reference gate when the
// game renames them. Holds are those defs: graves and sarcophagi for the tomb
// and morgue, dressers for the wardrobe and shelves for the armory.
//
// None is a game number. The gear rooms are 7x5, shelf walls on both long
// sides with a lane between; the battery room holds four batteries a side
// along a one-cell walkway. A room the colony outgrows is added again
// (RoomDemand), not enlarged.
type RoomSizeTable struct {
	Tomb, Morgue, Armory, Wardrobe, Battery [2]int32
	Holds                                   []string
}

var roomSizeTable = RoomSizeTable{
	Tomb: [2]int32{5, 5}, Morgue: [2]int32{5, 4},
	Armory: [2]int32{7, 5}, Wardrobe: [2]int32{7, 5},
	Battery: [2]int32{5, 7},
	Holds:   []string{"Grave", "Sarcophagus", "Dresser", "Shelf"},
}

// roomSize is role's interior from roomSizeTable; the zero value for a role
// the table does not size.
func roomSize(role PlannedRole) [2]int32 {
	t := roomSizeTable
	switch role {
	case PlannedTomb:
		return t.Tomb
	case PlannedMorgue:
		return t.Morgue
	case PlannedArmory:
		return t.Armory
	case PlannedWardrobe:
		return t.Wardrobe
	case PlannedBattery:
		return t.Battery
	}
	return [2]int32{}
}
