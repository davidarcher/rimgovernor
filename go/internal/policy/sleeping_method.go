package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SleepingMethod is what a MaintainSleeping deficit needs next: ownership of
// a vacant suitable bed for one colonist, or one more humanlike bed staged in
// a room that can host a Bedroom. Observed sleep in the assigned bed, never
// an assignment or construction receipt, completes the goal (ReviewSleeping).
type SleepingMethod string

const (
	// SleepingUnknown: a native fact the choice depends on is unobserved.
	SleepingUnknown SleepingMethod = ""
	// SleepingNoDemand: every colonist owns a suitable bed; the goal waits
	// on observed use.
	SleepingNoDemand SleepingMethod = "no_demand"
	// SleepingAssign: transfer ownership of Bed to Pawn (AssignBed).
	SleepingAssign SleepingMethod = "assign"
	// SleepingBuild: stage Definition in a Bedroom-hosting room whose
	// temperature suits the colonists still waiting; the next review assigns
	// it.
	SleepingBuild SleepingMethod = "build"
	// SleepingUnavailable: nobody can be assigned and no bed definition is
	// buildable now.
	SleepingUnavailable SleepingMethod = "unavailable"
)

// SleepingBedDefinitions lists, in preference order, the bed definitions the
// sleeping planner may stage. A sleeping spot is never suitable
// (ReviewSleeping), so it is not a fallback here.
var SleepingBedDefinitions = []string{"Bed"}

type SleepingRequest struct {
	Targets     domain.Fact[[]SleepingTarget]
	Sleeping    domain.Fact[SleepingObservation]
	Rooms       domain.Fact[RoomObservation]
	Definitions []BenchDefinition
}

type SleepingChoice struct {
	Method SleepingMethod
	// Waiting counts the colonists still without observed use of a suitable
	// owned bed; Unhoused counts those among them with no bed to be assigned,
	// the number of beds still to stage.
	Waiting, Unhoused int
	Pawn              PawnID
	Bed               string
	// PreviousBed is the pawn's currently owned bed the assignment expects,
	// empty when none.
	PreviousBed string
	Definition  string
	// Cells are the hosting-room cells whose observed temperature suits the
	// waiting colonists; a build is sited only there.
	Cells []domain.Cell
}

// SelectSleepingMethod picks one bounded step for the sleeping deficit.
// Assignment comes first (the lowest pawn ID with a vacant suitable bed, the
// lowest bed ID), since it costs nothing and the review already vetted the
// bed's roof, temperature and access for that pawn. Otherwise one bed is
// staged in a hosting room warm enough for the waiting colonists; a room too
// cold or hot for them is not a site, so the temperature family keeps that
// deficit. It issues no orders and reserves nothing.
func SelectSleepingMethod(r SleepingRequest) (SleepingChoice, error) {
	if len(r.Definitions) > 256 {
		return SleepingChoice{}, errors.New("sleeping request exceeds bound")
	}
	targets, known := r.Targets.Value()
	if !known {
		return SleepingChoice{Method: SleepingUnknown}, nil
	}
	if len(targets) > 256 {
		return SleepingChoice{}, errors.New("sleeping census exceeds bound")
	}
	choice := SleepingChoice{Waiting: len(targets)}
	if choice.Waiting == 0 {
		choice.Method = SleepingNoDemand
		return choice, nil
	}
	ordered := append([]SleepingTarget{}, targets...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].Pawn < ordered[j].Pawn })
	// A bed already owned by the pawn is never reassigned to itself; the
	// review lists only vacant beds as available, so that cannot arise, but
	// the assignment contract requires it.
	for _, t := range ordered {
		if t.Kind == SleepingUseNeeded || len(t.Available) == 0 {
			continue
		}
		beds := append([]string{}, t.Available...)
		sort.Strings(beds)
		for _, bed := range beds {
			if bed != t.PreviousBed {
				choice.Method, choice.Pawn, choice.Bed, choice.PreviousBed = SleepingAssign, t.Pawn, bed, t.PreviousBed
				return choice, nil
			}
		}
	}
	// Everyone still waiting either has a bed to use or has nothing to be
	// assigned; only the latter needs a bed built.
	lo, hi := -1e9, 1e9
	needsBed := false
	sleeping, sk := r.Sleeping.Value()
	if !sk {
		choice.Method = SleepingUnknown
		return choice, nil
	}
	if len(sleeping.People) > 256 {
		return SleepingChoice{}, errors.New("sleeping census exceeds bound")
	}
	band := map[PawnID][2]float64{}
	for _, p := range sleeping.People {
		min, mk := p.ComfortableMin.Value()
		max, xk := p.ComfortableMax.Value()
		if mk && xk {
			band[p.ID] = [2]float64{min, max}
		}
	}
	for _, t := range ordered {
		if t.Kind == SleepingUseNeeded {
			continue
		}
		needsBed = true
		choice.Unhoused++
		b, ok := band[t.Pawn]
		if !ok {
			choice.Method = SleepingUnknown
			return choice, nil
		}
		lo, hi = max(lo, b[0]), min(hi, b[1])
	}
	if !needsBed {
		choice.Method = SleepingNoDemand
		return choice, nil
	}
	facility, err := Facility(RoomRoleBedroom)
	if err != nil {
		return SleepingChoice{}, err
	}
	rooms, rk := r.Rooms.Value()
	if !rk {
		choice.Method = SleepingUnknown
		return choice, nil
	}
	if len(rooms.Rooms) > 256 {
		return SleepingChoice{}, errors.New("sleeping census exceeds bound")
	}
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if !known {
			choice.Method = SleepingUnknown
			return choice, nil
		}
		if !facility.Hosts(role) {
			continue
		}
		temperature, known := room.Temperature.Value()
		if !known {
			choice.Method = SleepingUnknown
			return choice, nil
		}
		if temperature < lo || temperature > hi {
			continue
		}
		choice.Cells = append(choice.Cells, room.Cells...)
	}
	byName := map[string]BenchDefinition{}
	for _, d := range r.Definitions {
		byName[d.Name] = d
	}
	unknown := false
	for _, name := range SleepingBedDefinitions {
		d, exists := byName[name]
		available, ak := d.Available.Value()
		if !exists || !ak {
			unknown = true
			continue
		}
		if available {
			choice.Method, choice.Definition = SleepingBuild, name
			return choice, nil
		}
	}
	if unknown {
		choice.Method = SleepingUnknown
		return choice, nil
	}
	choice.Method = SleepingUnavailable
	return choice, nil
}
