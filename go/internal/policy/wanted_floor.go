package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// WantedFloors is the flooring review's decision read per cell of a
// PlannedRoom, the reconciler's WantedFloor: the floor def
// each interior cell wants, "" for none. The floor choice stays flooring's
// (chooseFloor, the one the review's selector lays): the throne room takes the
// floor its title's tags name (tags are ThroneRequirements.FloorTags), any other
// room the tier its role requires, scored on the same economics. A room whose
// role needs no floor, or for which no floor is available, affordable or known
// yet, wants none; the whole room is priced as one batch, since the
// MaxCellsPerPlan batching only paces the laying of non-planned ground.
func WantedFloors(room PlannedRoom, tags []string, facts FlooringFacts, p FlooringPolicy) func(domain.Cell) string {
	none := func(domain.Cell) string { return "" }
	d, ok := roomFloorDeficit(room, tags)
	cells := d.Cells
	if room.Role == PlannedContainmentCell {
		// The containment cell takes the catalog's containment floor when the
		// stock pays for tiles not already laid or ordered, else no floor.
		if !containmentRoomFloorLaid(facts.ContainmentFloor, facts, room) {
			return none
		}
		return func(domain.Cell) string { return facts.ContainmentFloor.Def }
	}
	if !ok || len(cells) == 0 || !p.valid() {
		return none
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

// roomFloorDeficit is the floor requirement a PlannedRoom's whole interior
// carries: the tier its role asks for, the throne room's title tags.
func roomFloorDeficit(room PlannedRoom, tags []string) (FloorDeficit, bool) {
	role := moduleRoomRoles[room.Role]
	tier, ok := roleFloorTier(role)
	if len(tags) > 0 && role == RoomRoleThroneRoom {
		tier, ok = FloorTierThrone, true
	}
	d := FloorDeficit{Tier: tier, Role: role, Cells: rectCells(room.Interior)}
	if tier == FloorTierThrone {
		d.Tags = tags
	}
	return d, ok
}

// FloorKept reports whether the constructed floor have stands in for the wanted
// floor want of a PlannedRoom: any floor that meets the room's tier
// does, so a different adequate floor is never torn up. The throne room's title
// tags are the exception: the floor must carry one. A floor the mirror does not
// describe is kept, never torn up on a missing fact.
func FloorKept(room PlannedRoom, tags []string, facts FlooringFacts, p FlooringPolicy) func(have, want string) bool {
	d, ok := roomFloorDeficit(room, tags)
	return func(have, want string) bool {
		if have == want {
			return true
		}
		if room.Role == PlannedContainmentCell {
			// Only the containment floor stands in for it (replacing a plain
			// floor is what the strength was predicted on).
			return false
		}
		def, known := facts.Definitions[have]
		if !ok || !known || !p.valid() {
			return true
		}
		if d.Tier == FloorTierThrone {
			return hasAnyTag(def.Tags, d.Tags)
		}
		weights := p.Clean
		switch d.Tier {
		case FloorTierLiving:
			weights = p.Living
		}
		_, meets := floorScore(d.Tier, def, weights)
		return meets
	}
}

// RoomFloors is the flooring review's decision for one PlannedRoom, for the
// reconciler: the wanted floor per interior cell and whether an existing floor
// stands in for it. A nil RoomFloors wants no floor anywhere.
type RoomFloors func(PlannedRoom) (wanted func(domain.Cell) string, kept func(have, want string) bool)

// FlooringRoomFloors reads WantedFloors and FloorKept for every room; tags names
// a room's title tags (the throne room's), nil for none.
func FlooringRoomFloors(tags func(PlannedRoom) []string, facts FlooringFacts, p FlooringPolicy) RoomFloors {
	return func(r PlannedRoom) (func(domain.Cell) string, func(have, want string) bool) {
		var t []string
		if tags != nil {
			t = tags(r)
		}
		return WantedFloors(r, t, facts, p), FloorKept(r, t, facts, p)
	}
}
