package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SleepingMethod is what a MaintainHousing deficit needs next: ownership of
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
	// SleepingAssign: transfer ownership of Bed to Pawn (BedAssignIntent).
	SleepingAssign SleepingMethod = "assign"
	// SleepingBuild: stage Definition in a Bedroom-hosting room whose
	// temperature suits the colonists still waiting; the next review assigns
	// it.
	SleepingBuild SleepingMethod = "build"
	// SleepingMarkSlaves: set Bed, a vacant colonist bed that would suit
	// the waiting slave Pawn, for slaves (BuildingPatchIntent for_slaves);
	// the next review assigns it.
	SleepingMarkSlaves SleepingMethod = "mark_slaves"
	// SleepingUnavailable: nobody can be assigned and no bed definition is
	// buildable now.
	SleepingUnavailable SleepingMethod = "unavailable"
)

// SleepingBedDefinitions lists, in preference order, the bed definitions the
// sleeping planner may stage (#1181): a bed, a bedroll once its stuff is on
// hand, else a sleeping spot (bedroom furnishing only, #1182). A bedroll is
// suitable only while Bed is unavailable, a spot never (ReviewSleeping).
var SleepingBedDefinitions = []string{"Bed", SleepingBedrollDefinition, SleepingSpotDefinition}

// SleepingCoupleBedDefinition is staged first when a waiting colonist has a
// couple partner (#812).
const SleepingCoupleBedDefinition = "DoubleBed"

const (
	SleepingBedrollDefinition       = "Bedroll"
	SleepingCoupleBedrollDefinition = "BedrollDouble"
	SleepingSpotDefinition          = "SleepingSpot"
)

// sleepingBedrolls are the definitions staged only with their stuff on hand.
var sleepingBedrolls = map[string]bool{SleepingBedrollDefinition: true, SleepingCoupleBedrollDefinition: true}

// SleepingLadder is the definitions a bed step may stage, in preference
// order; a couple's double of each rung comes first.
func SleepingLadder(couple bool) []string {
	if couple {
		return []string{SleepingCoupleBedDefinition, "Bed", SleepingCoupleBedrollDefinition, SleepingBedrollDefinition, SleepingSpotDefinition}
	}
	return append([]string(nil), SleepingBedDefinitions...)
}

// SleepingDefinition is the best buildable rung of the ladder: the first
// available definition, a bedroll only when Stocked names it. Unknown is
// returned when an unknown row precedes it and nothing is buildable. A
// sleeping spot is never a suitable bed (#1182), so only spot true, a
// bedroom furnished to move a spot owner in, walks down to it.
func SleepingDefinition(definitions []BenchDefinition, stocked map[string]bool, couple, spot bool) (string, SleepingMethod) {
	byName := map[string]BenchDefinition{}
	for _, d := range definitions {
		byName[d.Name] = d
	}
	unknown := false
	for _, name := range SleepingLadder(couple) {
		if name == SleepingSpotDefinition && !spot {
			continue
		}
		d, exists := byName[name]
		available, ak := d.Available.Value()
		if !exists || !ak {
			unknown = true
			continue
		}
		if available && (!sleepingBedrolls[name] || stocked[name]) {
			return name, SleepingBuild
		}
	}
	if unknown {
		return "", SleepingUnknown
	}
	return "", SleepingUnavailable
}

type SleepingRequest struct {
	Targets     domain.Fact[[]SleepingTarget]
	Sleeping    domain.Fact[SleepingObservation]
	Rooms       domain.Fact[RoomObservation]
	Definitions []BenchDefinition
	// Stocked names the bedroll definitions whose stuff is on hand.
	Stocked map[string]bool
	// RoomTargets (RoomQualityTargets, keyed by room) and Traits order the
	// beds an assignment offers (#813): a room marked NeverUpgrade is never
	// left for a more impressive one, and an ascetic takes the plainest bed.
	// Nil leaves the lowest-bed-ID order.
	RoomTargets map[string]RoomTarget
	Traits      map[PawnID]TraitEffects
}

// sleepingBedOrder filters and orders the vacant beds offered to t.
func sleepingBedOrder(r SleepingRequest, t SleepingTarget, beds []string) []string {
	sleeping, known := r.Sleeping.Value()
	if !known || (r.RoomTargets == nil && r.Traits == nil) {
		return beds
	}
	impressiveness := map[string]float64{}
	if rooms, ok := sleeping.Rooms.Value(); ok {
		for _, room := range rooms {
			if q, ok := room.Quality.Value(); ok {
				impressiveness[room.ID] = q.Impressiveness
			}
		}
	}
	quality := map[string]float64{}
	roomOf := map[string]string{}
	for _, b := range sleeping.Beds {
		if room, ok := b.Room.Value(); ok {
			roomOf[b.ID] = room
			if v, ok := impressiveness[room]; ok {
				quality[b.ID] = v
			}
		}
	}
	if target, ok := r.RoomTargets[roomOf[t.PreviousBed]]; ok && t.PreviousBed != "" && target.NeverUpgrade {
		current, ck := quality[t.PreviousBed]
		kept := []string{}
		for _, bed := range beds {
			v, vk := quality[bed]
			if vk && (target.Max <= 0 || v < target.Max) && (!ck || v <= current) {
				kept = append(kept, bed)
			}
		}
		beds = kept
	}
	if r.Traits[t.Pawn].Ascetic {
		sort.SliceStable(beds, func(i, j int) bool {
			a, ak := quality[beds[i]]
			b, bk := quality[beds[j]]
			if ak != bk {
				return ak
			}
			return ak && a < b
		})
	}
	return beds
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
	targets, known := r.Targets.Value()
	if !known {
		return SleepingChoice{Method: SleepingUnknown}, nil
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
		// The review lists beds in preference order (a couple's double bed
		// first); room targets and traits reorder stably.
		beds := sleepingBedOrder(r, t, append([]string{}, t.Available...))
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
	needsBed, couple := false, false
	sleeping, sk := r.Sleeping.Value()
	if !sk {
		choice.Method = SleepingUnknown
		return choice, nil
	}
	if pawn, bed, ok := slaveBedToMark(ordered, sleeping); ok {
		choice.Method, choice.Pawn, choice.Bed = SleepingMarkSlaves, pawn, bed
		return choice, nil
	}
	band := map[PawnID][2]float64{}
	for _, p := range append(append([]SleepingPerson{}, sleeping.People...), sleeping.Slaves...) {
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
		couple = couple || t.Partner != ""
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
	choice.Definition, choice.Method = SleepingDefinition(r.Definitions, r.Stocked, couple, false)
	return choice, nil
}

// slaveBedToMark is the first waiting slave with nothing to be assigned and
// the lowest-ID vacant colonist bed that would suit it set for slaves: a
// standing bed no waiting colonist can take (assignment already ran), so
// marking it costs no colonist a bed. None leaves the build path to stage
// one more bed, which a later review marks.
func slaveBedToMark(targets []SleepingTarget, sleeping SleepingObservation) (PawnID, string, bool) {
	slaves := map[PawnID]SleepingPerson{}
	for _, p := range sleeping.Slaves {
		slaves[p.ID] = p
	}
	beds := append([]SleepingBed{}, sleeping.Beds...)
	sort.Slice(beds, func(i, j int) bool { return beds[i].ID < beds[j].ID })
	for _, t := range targets {
		p, ok := slaves[t.Pawn]
		if !ok || t.Kind == SleepingUseNeeded || len(t.Available) != 0 {
			continue
		}
		lo, lk := p.ComfortableMin.Value()
		hi, hk := p.ComfortableMax.Value()
		if !lk || !hk {
			continue
		}
		for _, b := range beds {
			human, _ := b.Humanlike.Value()
			medical, mk := b.Medical.Value()
			prisoner, pk := b.Prisoners.Value()
			roof, _ := b.Roofed.Value()
			rest, _ := b.RestEffectiveness.Value()
			temperature, tk := b.Temperature.Value()
			if !human || !mk || medical || !pk || prisoner || b.Slaves || !roof || len(b.Owners) != 0 || b.Definition == "SleepingSpot" || rest <= 0 || !tk || temperature < lo || temperature > hi || !slices.Contains(b.AccessibleTo, p.ID) {
				continue
			}
			return p.ID, b.ID, true
		}
	}
	return "", "", false
}
