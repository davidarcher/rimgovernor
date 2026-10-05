package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// WantedFloors is the flooring review's decision read per cell of a
// PlannedRoom (#2107, epic #2101), the reconciler's WantedFloor: the floor def
// each interior cell wants, "" for none. The floor choice stays flooring's
// (chooseFloor, the one the review's selector lays): the throne room takes the
// floor its title's tags name (tags are ThroneRequirements.FloorTags), any other
// room the tier its role requires, scored on the same economics. A room whose
// role needs no floor, or for which no floor is available, affordable or known
// yet, wants none; the whole room is priced as one batch, since the
// MaxCellsPerPlan batching only paces the laying of non-planned ground.
func WantedFloors(room PlannedRoom, tags []string, facts FlooringFacts, p FlooringPolicy) func(domain.Cell) string {
	none := func(domain.Cell) string { return "" }
	role := moduleRoomRoles[room.Role]
	tier, ok := roleFloorTier(role)
	if len(tags) > 0 && role == RoomRoleThroneRoom {
		tier, ok = FloorTierThrone, true
	}
	cells := rectCells(room.Interior)
	if !ok || len(cells) == 0 || !p.valid() {
		return none
	}
	d := FloorDeficit{Tier: tier, Role: role, Cells: cells}
	if tier == FloorTierThrone {
		d.Tags = tags
	}
	name, _, _ := chooseFloor(d, facts, len(cells), p)
	if name == "" {
		return none
	}
	in := map[domain.Cell]bool{}
	for _, c := range cells {
		in[c] = true
	}
	return func(c domain.Cell) string {
		if in[c] {
			return name
		}
		return ""
	}
}
