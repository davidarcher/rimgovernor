package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Wing migration. After a tier bump a smaller bedroom
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
			// Census: the room's beds are the retiring beds.
			if room, ok := CensusRoomIn(r, rooms); ok {
				for _, b := range room.Beds {
					out[b] = true
				}
			}
		}
	}
	return out
}

// EmptiedRetiringWings is the Retiring wings no pawn owns a bed in, keyed
// by their corridor's hallway cell (ReplanLayoutWithRooms's emptied): every room
// of the wing is unbuilt or holds only unowned beds. None while the census
// or sleeping read is unknown.
func EmptiedRetiringWings(plan LayoutPlan, rooms domain.Fact[RoomObservation], sleeping domain.Fact[SleepingObservation]) map[domain.Cell]bool {
	census, rk := rooms.Value()
	obs, sk := sleeping.Value()
	if !rk || !sk {
		return nil
	}
	owned := map[string]bool{}
	for _, b := range obs.Beds {
		if len(b.Owners) > 0 {
			owned[b.ID] = true
		}
	}
	out := map[domain.Cell]bool{}
	for _, w := range plan.Wings {
		if w.Purpose != WingBedroomsRetiring {
			continue
		}
		empty := true
		for _, r := range w.Rooms {
			// Census: the room's beds say whether the wing is empty.
			if room, ok := CensusRoomIn(r, census); ok {
				for _, b := range room.Beds {
					empty = empty && !owned[b]
				}
			}
		}
		if empty {
			out[w.Corridor.From] = true
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
// none vacant, reconcile the first standing empty active room, else the
// first unbuilt one (BedroomReconcile). BedroomNone when no single pawn sleeps in a
// Retiring room (a couple keeps its double bed). One pawn per step.
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
	var empty, unbuilt []PlannedRoom
	var vacant []string
	for _, w := range plan.Wings {
		if w.Purpose != WingBedrooms {
			continue
		}
		for _, r := range w.Rooms {
			// Census: the room's beds say vacant, empty or unbuilt.
			room, ok := CensusRoomIn(r, rooms)
			if !ok {
				unbuilt = append(unbuilt, r)
				continue
			}
			if len(room.Beds) == 0 {
				empty = append(empty, r)
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
	if rooms := append(empty, unbuilt...); len(rooms) > 0 {
		return BedroomStep{Kind: BedroomReconcile, Room: rooms[0]}
	}
	return BedroomStep{}
}
