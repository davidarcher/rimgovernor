package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Individual bedrooms (#786, C2). The sleeping planner walks each
// colonist, a bedless joiner included (#1197), into a room of their own in the bedroom wing, in the wing's slot
// order (#1213): raise the room's shell, stage one bed in it, then
// move the colonist's ownership there. The shelter bed left behind stays
// as a spare for joiners (MaintainHousing keeps one beyond the population).

// ShellBedIDs are the beds standing in the starter shell, the planned
// shelter (#2037).
func ShellBedIDs(plan LayoutPlan, rooms RoomObservation) map[string]bool {
	shell := map[string]bool{}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedShelter {
			continue
		}
		if room, ok := CensusRoomIn(r, rooms); ok {
			for _, b := range room.Beds {
				shell[b] = true
			}
		}
	}
	return shell
}

// BedroomStepKind is the next bedroom step.
type BedroomStepKind string

const (
	// BedroomNone: every colonist owns a bed in a Bedroom-role room, or no planned
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
	// BedroomClear: deconstruct Bed, a vacant sleeping spot left in the
	// starter shell (the planned shelter) at Cells[0] (#1182).
	BedroomClear BedroomStepKind = "clear"
)

// BedroomStep is one bounded step towards individual bedrooms.
type BedroomStep struct {
	Kind             BedroomStepKind
	Pawn             PawnID
	Bed, PreviousBed string
	Room             PlannedRoom
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
func NextBedroomStep(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget, traits map[PawnID]TraitEffects, pressure map[PawnID]float64, gate RoomGate) BedroomStep {
	if len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return BedroomStep{}
	}
	// The starter shell is the planned shelter (#2037): the
	// last spot left in it reads as a bedroom but is still the shell.
	shell := ShellBedIDs(plan, rooms)
	bedroomBed := map[string]bool{}
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleBedroom {
			for _, b := range room.Beds {
				bedroomBed[b] = !shell[b]
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
		if !known {
			return BedroomStep{}
		}
		// A colonist with no bed is unhoused too (#1197): with nothing
		// better buildable they get a bedroom shell and a spot.
		if bed == "" {
			unhoused = append(unhoused, p)
			continue
		}
		// A couple is never split into single bedrooms: sleeping upkeep
		// would reunite them at once (#838).
		if _, coupled := couples[p.ID]; !coupled && !bedroomBed[bed] && !kept[bed] {
			unhoused = append(unhoused, p)
		}
	}
	// A spot its owner left behind in the shell is taken down (#1182), so
	// the shell ends with no bunks.
	// Only a spare one: a spot is never an owned bed (#1182), so a spot just
	// built for the shelter reads vacant too, and clearing it re-opened the
	// shelter deficit until the epoch's bed cap ran out one spot short
	// (campaign/foothold, #1266). The clear leaves an indoor bed for every
	// colonist.
	indoor := 0
	for _, room := range rooms.Rooms {
		if enclosed, known := room.Enclosed.Value(); known && enclosed {
			for _, id := range room.Beds {
				if b, ok := beds[id]; ok {
					if human, _ := b.Humanlike.Value(); human {
						indoor++
					}
				}
			}
		}
	}
	var left []string
	for id := range shell {
		if b, ok := beds[id]; ok && b.Definition == SleepingSpotDefinition && len(b.Owners) == 0 {
			left = append(left, id)
		}
	}
	if len(left) > 0 && indoor > len(people) {
		sort.Strings(left)
		b := beds[left[0]]
		return BedroomStep{Kind: BedroomClear, Bed: b.ID, Cells: []domain.Cell{b.Cell}, Unhoused: len(unhoused)}
	}
	// Once the standard rooms have nothing to do, a qualifying pawn is
	// walked into a suite (#1216).
	suiteStep := func() BedroomStep {
		step := nextSuiteStep(plan, rooms, sleeping, SuiteClaims(plan, rooms, sleeping, targets, traits, pressure, gate))
		step.Unhoused = len(unhoused)
		return step
	}
	if len(unhoused) == 0 {
		return suiteStep()
	}
	// A suite's bed is its claimant's, never an unhoused pawn's.
	suiteBed := map[string]bool{}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedSuite {
			continue
		}
		if room, ok := CensusRoomIn(r, rooms); ok {
			for _, b := range room.Beds {
				suiteBed[b] = true
			}
		}
	}
	retiringBed := retiringBeds(plan, rooms)
	vacant := []string{}
	for id, housed := range bedroomBed {
		b, ok := beds[id]
		// A Retiring room's bed takes no one new (#1219).
		if housed && ok && !suiteBed[id] && vacantColonistBed(b) && !retiringBed[id] {
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
	standing := func(r PlannedRoom) (Room, bool) { return CensusRoomIn(r, rooms) }
	var empty []PlannedRoom
	var emptyCells [][]domain.Cell
	var unbuilt []PlannedRoom
	retiring := retiringRooms(plan)
	for _, r := range plan.AllRooms() {
		// A Retiring wing is never built out further (#1219).
		if r.Role != PlannedBedroom || retiring[r.Interior] {
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
	// No slot left: Unhoused still counts who stays outside a bedroom.
	return suiteStep()
}

// BedResearch unlocks Bed and DoubleBed.
const BedResearch = "ComplexFurniture"

// BedResearchRequest appends BedResearch to needs once every colonist owns a
// bed in an individual bedroom (NextBedroomStep has no step left and nobody
// is unhoused) while Bed is known unavailable (#1183). The bed ladder then
// upgrades bedrolls and spots once it is researched (#1181).
func BedResearchRequest(needs []string, plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget) []string {
	if buildable, known := sleeping.BedBuildable.Value(); !known || buildable {
		return needs
	}
	if len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return needs
	}
	for _, p := range sleeping.People {
		if bed, known := p.OwnedBed.Value(); !known || bed == "" {
			return needs
		}
	}
	if step := NextBedroomStep(plan, rooms, sleeping, targets, nil, nil, RoomGate{}); step.Kind != BedroomNone && step.Room.Role != PlannedSuite || step.Unhoused > 0 {
		return needs
	}
	return append(append([]string(nil), needs...), BedResearch)
}

// BedroomsOwed is the review's bedroom deficit: known true while a bedroom
// step is due, unknown while the plan, room or sleeping census is.
func BedroomsOwed(plan domain.Fact[LayoutPlan], rooms domain.Fact[RoomObservation], sleeping domain.Fact[SleepingObservation], targets map[string]RoomTarget, traits map[PawnID]TraitEffects, pressure map[PawnID]float64, gate RoomGate) domain.Fact[bool] {
	p, pk := plan.Value()
	r, rk := rooms.Value()
	s, sk := sleeping.Value()
	if !pk || !rk || !sk {
		return domain.Unknown[bool]()
	}
	return domain.Known(NextBedroomStep(p, r, s, targets, traits, pressure, gate).Kind != BedroomNone)
}

func containsPawn(ids []PawnID, id PawnID) bool {
	for _, p := range ids {
		if p == id {
			return true
		}
	}
	return false
}
