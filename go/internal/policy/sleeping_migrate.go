package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Bedroom migration. A bedroom a successor replaces (layout_succession.go), or
// one in a smaller wing a tier bump left Retiring (retireWings), is retiring:
// its pawns move one at a time into the other bedrooms. The move is not a
// bedroom deficit: the unhoused count, BedroomsOwed and the bed research
// request ignore it.

// retiringBedrooms are the bedrooms whose pawns migrate: those a successor
// replaces and the rooms of the Retiring wings.
func retiringBedrooms(plan LayoutPlan) []PlannedRoom {
	gone := plan.superseded()
	inWing := map[int32]bool{}
	var out []PlannedRoom
	for _, w := range plan.Wings {
		if w.Purpose == WingBedroomsRetiring {
			for _, r := range w.Rooms {
				inWing[r.ID] = true
				out = append(out, r)
			}
		}
	}
	for _, r := range plan.AllRooms() {
		if (r.Role == PlannedBedroom || r.Role == PlannedSuite) && gone[r.ID] && !inWing[r.ID] {
			out = append(out, r)
		}
	}
	return out
}

// retiringRooms is the interiors of the retiring bedrooms.
func retiringRooms(plan LayoutPlan) map[Rectangle]bool {
	out := map[Rectangle]bool{}
	for _, r := range retiringBedrooms(plan) {
		out[r.Region.Bounds()] = true
	}
	return out
}

// retiringBeds is the beds standing in the retiring bedrooms.
func retiringBeds(plan LayoutPlan, rooms RoomObservation) map[string]bool {
	out := map[string]bool{}
	for b := range retiringBedRoles(plan, rooms) {
		out[b] = true
	}
	return out
}

// retiringBedRoles is the retiring beds with the planned role of their room: a
// pawn leaving a suite moves to a suite, one leaving a bedroom to a bedroom.
func retiringBedRoles(plan LayoutPlan, rooms RoomObservation) map[string]PlannedRole {
	out := map[string]PlannedRole{}
	for _, r := range retiringBedrooms(plan) {
		// Census: the room's beds are the retiring beds.
		if room, ok := CensusRoomIn(r, rooms); ok {
			for _, b := range room.Beds {
				out[b] = r.Role
			}
		}
	}
	return out
}

// SleptBedrooms are the bedrooms a successor replaces that a pawn still owns a
// bed in: they stay in the plan until the pawn has moved. With the census or
// the sleeping read unknown every replaced bedroom counts, so none leaves
// unseen.
func SleptBedrooms(plan LayoutPlan, rooms domain.Fact[RoomObservation], sleeping domain.Fact[SleepingObservation]) map[int32]bool {
	census, rk := rooms.Value()
	obs, sk := sleeping.Value()
	owned := map[string]bool{}
	for _, b := range obs.Beds {
		if len(b.Owners) > 0 {
			owned[b.ID] = true
		}
	}
	gone := plan.superseded()
	var out map[int32]bool
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedBedroom && r.Role != PlannedSuite || !gone[r.ID] {
			continue
		}
		slept := !rk || !sk
		if room, ok := CensusRoomIn(r, census); ok && rk && sk {
			for _, b := range room.Beds {
				slept = slept || owned[b]
			}
		}
		if slept {
			if out == nil {
				out = map[int32]bool{}
			}
			out[r.ID] = true
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
	old := retiringBedRoles(plan, rooms)
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
		if bed, known := p.OwnedBed.Value(); known && old[bed] != "" {
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
	// A pawn leaving a suite moves to a suite and one leaving a bedroom to a
	// bedroom, so the targets are kept by the planned role.
	empty, unbuilt := map[PlannedRole][]PlannedRoom{}, map[PlannedRole][]PlannedRoom{}
	vacant := map[PlannedRole][]string{}
	retiring := retiringRooms(plan)
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedBedroom && r.Role != PlannedSuite || retiring[r.Region.Bounds()] {
			continue
		}
		// Census: the room's beds say vacant, empty or unbuilt.
		room, ok := CensusRoomIn(r, rooms)
		if !ok {
			unbuilt[r.Role] = append(unbuilt[r.Role], r)
			continue
		}
		if len(room.Beds) == 0 {
			empty[r.Role] = append(empty[r.Role], r)
		}
		for _, id := range room.Beds {
			if b, ok := beds[id]; ok && vacantColonistBed(b) {
				vacant[r.Role] = append(vacant[r.Role], id)
			}
		}
	}
	for _, ids := range vacant {
		sort.Strings(ids)
	}
	for _, p := range movers {
		previous, _ := p.OwnedBed.Value()
		for _, id := range vacant[old[previous]] {
			if containsPawn(beds[id].AccessibleTo, p.ID) {
				return BedroomStep{Kind: BedroomMove, Pawn: p.ID, Bed: id, PreviousBed: previous}
			}
		}
	}
	for _, p := range movers {
		previous, _ := p.OwnedBed.Value()
		role := old[previous]
		if rooms := append(slices.Clone(empty[role]), unbuilt[role]...); len(rooms) > 0 {
			return BedroomStep{Kind: BedroomReconcile, Room: rooms[0]}
		}
	}
	return BedroomStep{}
}
