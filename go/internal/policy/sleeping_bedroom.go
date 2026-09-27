package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Individual bedrooms (#786, C2). Once every colonist owns a bed (the
// initial shelter and its barracks stand), the sleeping planner walks each
// colonist into a planned 5x5 bedroom of their own, in the plan's slot
// order along the spine: raise the room's shell, stage one bed in it, then
// move the colonist's ownership there. The barracks bed left behind stays
// as a spare for joiners (MaintainHousing keeps one beyond the population).

// BedroomStepKind is the next bedroom step.
type BedroomStepKind string

const (
	// BedroomNone: every colonist owns a bed in a Bedroom-role room, some
	// colonist has no bed yet (the barracks comes first), or no planned
	// bedroom is left to build.
	BedroomNone BedroomStepKind = ""
	// BedroomMove: assign Bed, a vacant bed in a Bedroom-role room, to Pawn.
	BedroomMove BedroomStepKind = "move"
	// BedroomFurnish: stage one bed in Room, a planned bedroom standing
	// enclosed with no bed; Cells are its observed floor.
	BedroomFurnish BedroomStepKind = "furnish"
	// BedroomShell: raise the walls and door of Room, a planned bedroom not
	// standing yet.
	BedroomShell BedroomStepKind = "shell"
)

// BedroomStep is one bounded step towards individual bedrooms.
type BedroomStep struct {
	Kind             BedroomStepKind
	Pawn             PawnID
	Bed, PreviousBed string
	Room             LayoutRoom
	Cells            []domain.Cell
	Unhoused         int
}

// NextBedroomStep picks the next bedroom step from the plan, the room census
// and the sleeping census. Move comes first (it costs nothing), then a bed in
// a standing empty bedroom, then a new shell, and a shell only while the
// standing bedrooms cannot take every colonist still outside one; a room dug
// into rock is a shell too, whose builder mines it first (#836). It reports BedroomNone whenever a
// fact it needs is unknown.
// A colonist whose current room RoomTargets marks NeverUpgrade (an ascetic,
// #826) counts as housed: the move never takes them from the plainest room.
func NextBedroomStep(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget) BedroomStep {
	if len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return BedroomStep{}
	}
	bedroomBed := map[string]bool{}
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleBedroom {
			for _, b := range room.Beds {
				bedroomBed[b] = true
			}
		}
	}
	beds := map[string]SleepingBed{}
	kept := map[string]bool{}
	for _, b := range sleeping.Beds {
		beds[b.ID] = b
		if room, ok := b.Room.Value(); ok && targets[room].NeverUpgrade {
			kept[b.ID] = true
		}
	}
	people := append([]SleepingPerson(nil), sleeping.People...)
	sort.Slice(people, func(i, j int) bool { return people[i].ID < people[j].ID })
	couples := sleepingCouples(sleeping.People)
	var unhoused []SleepingPerson
	for _, p := range people {
		bed, known := p.OwnedBed.Value()
		if !known || bed == "" {
			// Barracks stays the fallback until everyone has a bed.
			return BedroomStep{}
		}
		// A couple is never split into single bedrooms: sleeping upkeep
		// would reunite them at once (#838).
		if _, coupled := couples[p.ID]; !coupled && !bedroomBed[bed] && !kept[bed] {
			unhoused = append(unhoused, p)
		}
	}
	if len(unhoused) == 0 {
		return BedroomStep{}
	}
	vacant := []string{}
	for id := range bedroomBed {
		b, ok := beds[id]
		if !ok || len(b.Owners) > 0 {
			continue
		}
		human, hk := b.Humanlike.Value()
		medical, mk := b.Medical.Value()
		prisoner, pk := b.Prisoners.Value()
		if hk && human && mk && !medical && pk && !prisoner {
			vacant = append(vacant, id)
		}
	}
	sort.Strings(vacant)
	for _, p := range unhoused {
		for _, id := range vacant {
			if containsPawn(beds[id].AccessibleTo, p.ID) {
				previous, _ := p.OwnedBed.Value()
				return BedroomStep{Kind: BedroomMove, Pawn: p.ID, Bed: id, PreviousBed: previous, Unhoused: len(unhoused)}
			}
		}
	}
	standing := func(r LayoutRoom) (Room, bool) { return PlannedRoomStanding(r, rooms) }
	var empty []LayoutRoom
	var emptyCells [][]domain.Cell
	var unbuilt []LayoutRoom
	for _, r := range plan.Rooms {
		if r.Role != ModuleBedroom {
			continue
		}
		room, ok := standing(r)
		if !ok {
			unbuilt = append(unbuilt, r)
			continue
		}
		if len(room.Beds) == 0 {
			empty = append(empty, r)
			emptyCells = append(emptyCells, room.Cells)
		}
	}
	if len(empty) > 0 {
		return BedroomStep{Kind: BedroomFurnish, Room: empty[0], Cells: emptyCells[0], Unhoused: len(unhoused)}
	}
	if len(unbuilt) > 0 {
		return BedroomStep{Kind: BedroomShell, Room: unbuilt[0], Unhoused: len(unhoused)}
	}
	return BedroomStep{}
}

// BedroomsOwed is the review's bedroom deficit: known true while a bedroom
// step is due, unknown while the plan, room or sleeping census is.
func BedroomsOwed(plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], sleeping domain.Fact[SleepingObservation], targets map[string]RoomTarget) domain.Fact[bool] {
	p, pk := plan.Value()
	r, rk := rooms.Value()
	s, sk := sleeping.Value()
	if !pk || !rk || !sk {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextBedroomStep(p, r, s, targets).Kind != BedroomNone)
}

func containsPawn(ids []PawnID, id PawnID) bool {
	for _, p := range ids {
		if p == id {
			return true
		}
	}
	return false
}
