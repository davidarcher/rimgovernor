package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Wing migration (#1219, epic #1200). After a tier bump a smaller bedroom
// wing is Retiring (retireWings); its pawns move one at a time into the
// active wings' rooms. The move is not a bedroom deficit: the unhoused
// count, BedroomsOwed and the bed research request ignore it.

// retiringRooms is the interiors of the Retiring wings' rooms.
func retiringRooms(plan LayoutPlan) map[Rectangle]bool {
	out := map[Rectangle]bool{}
	for _, w := range plan.Wings {
		if w.Purpose == WingBedroomsRetiring {
			for _, r := range w.Rooms {
				out[r.Interior] = true
			}
		}
	}
	return out
}

// retiringBeds is the beds standing in the Retiring wings' rooms.
func retiringBeds(plan LayoutPlan, rooms RoomObservation) map[string]bool {
	out := map[string]bool{}
	for _, w := range plan.Wings {
		if w.Purpose != WingBedroomsRetiring {
			continue
		}
		for _, r := range w.Rooms {
			if room, ok := PlannedRoomStanding(r, rooms); ok {
				for _, b := range room.Beds {
					out[b] = true
				}
			}
		}
	}
	return out
}

// vacantColonistBed is whether b is an unowned colonist bed: humanlike,
// not medical, not for prisoners or slaves.
func vacantColonistBed(b SleepingBed) bool {
	human, hk := b.Humanlike.Value()
	medical, mk := b.Medical.Value()
	prisoner, pk := b.Prisoners.Value()
	return len(b.Owners) == 0 && hk && human && mk && !medical && pk && !prisoner && !b.Slaves
}

// NextMigrateStep is the next migration step: move a pawn owning a bed in
// a Retiring wing's room to a vacant bed in an active wing's room; with
// none vacant, furnish the first standing empty active room, else shell
// the first unbuilt one. BedroomNone when no single pawn sleeps in a
// Retiring room (a couple keeps its double bed, #838). One pawn per step.
func NextMigrateStep(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation) BedroomStep {
	old := retiringBeds(plan, rooms)
	if len(old) == 0 {
		return BedroomStep{}
	}
	people := append([]SleepingPerson(nil), sleeping.People...)
	sort.Slice(people, func(i, j int) bool { return people[i].ID < people[j].ID })
	couples := sleepingCouples(sleeping.People)
	var movers []SleepingPerson
	for _, p := range people {
		if _, coupled := couples[p.ID]; coupled {
			continue
		}
		if bed, known := p.OwnedBed.Value(); known && old[bed] {
			movers = append(movers, p)
		}
	}
	if len(movers) == 0 {
		return BedroomStep{}
	}
	beds := map[string]SleepingBed{}
	for _, b := range sleeping.Beds {
		beds[b.ID] = b
	}
	var empty, unbuilt []LayoutRoom
	var emptyCells [][]domain.Cell
	var vacant []string
	for _, w := range plan.Wings {
		if w.Purpose != WingBedrooms {
			continue
		}
		for _, r := range w.Rooms {
			room, ok := PlannedRoomStanding(r, rooms)
			if !ok {
				unbuilt = append(unbuilt, r)
				continue
			}
			if len(room.Beds) == 0 {
				empty = append(empty, r)
				emptyCells = append(emptyCells, room.Cells)
			}
			for _, id := range room.Beds {
				if b, ok := beds[id]; ok && vacantColonistBed(b) {
					vacant = append(vacant, id)
				}
			}
		}
	}
	sort.Strings(vacant)
	for _, p := range movers {
		for _, id := range vacant {
			if containsPawn(beds[id].AccessibleTo, p.ID) {
				previous, _ := p.OwnedBed.Value()
				return BedroomStep{Kind: BedroomMove, Pawn: p.ID, Bed: id, PreviousBed: previous}
			}
		}
	}
	if len(empty) > 0 {
		return BedroomStep{Kind: BedroomFurnish, Room: empty[0], Cells: emptyCells[0]}
	}
	if len(unbuilt) > 0 {
		return BedroomStep{Kind: BedroomShell, Room: unbuilt[0]}
	}
	return BedroomStep{}
}
