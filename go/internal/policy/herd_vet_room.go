package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// VetRoomAreaKey is the bot area key of the VetRoom allowed area: the
// interior of the plan's vet rooms. It is the area SterilizeChoice lets an
// animal into; the barn reservations and the Safe area leave it out.
const VetRoomAreaKey = "VetRoom"

// VetRoomAreaLabel is the native label of the VetRoom area.
const VetRoomAreaLabel = VetRoomAreaKey

// VetRoomCells are the interior cells of the plan's vet rooms, sorted.
func (p LayoutPlan) VetRoomCells() []domain.Cell {
	set := map[domain.Cell]bool{}
	for _, room := range p.HerdRooms(PlannedVetRoom) {
		for _, c := range rectCells(room.Interior) {
			set[c] = true
		}
	}
	return sortedCells(set)
}

// VetRoomReady is VetRoom.Ready: known true when a vet room of the plan
// stands shelled (CensusRoomIn) with at least one standing animal bed
// whose census row reads medical. Known false when the plan has no vet room,
// none stands, or none of its standing beds is flagged. Unknown when a
// standing bed has no census row or an unread medical flag and no other bed
// proves the room ready.
func VetRoomReady(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, beds []SleepingBed, animalBed string) domain.Fact[bool] {
	unread := false
	for _, room := range plan.HerdRooms(PlannedVetRoom) {
		// Census: a vet room is ready only once roofed.
		if _, standing := CensusRoomIn(room, rooms); !standing {
			continue
		}
		for _, b := range built {
			if b.Building.Definition() != animalBed || len(b.Cells) == 0 || !rectInside(room.Interior, cellsRectangle(b.Cells)) {
				continue
			}
			read := false
			for _, s := range beds {
				if s.ID != b.ID {
					continue
				}
				medical, known := s.Medical.Value()
				if known && medical {
					return domain.Known(true)
				}
				read = read || known
			}
			unread = unread || !read
		}
	}
	if unread {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}
