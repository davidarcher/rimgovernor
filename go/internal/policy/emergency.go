package policy

import (
	"errors"
	"math"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type PawnID string

// MentalState is an observed active native break, never an inferred mood risk.
type MentalState struct {
	DefName      string
	IsAggro      bool
	TicksInState int32
}

type EmergencyPawn struct {
	MentalState                       domain.Fact[MentalState]
	ID                                PawnID
	Dead, Downed, Bleeding, NeedsTend domain.Fact[bool]
}
type ThreatKind uint8

const (
	Hostile ThreatKind = iota + 1
	HuntingPredator
	IgnoredHunter
	NearbyPredator
	NearbyDowned
	// HostileBuilding is a hostile-faction building the census lists as a
	// combat target in its own right (an insect hive, a crashed ship part):
	// a squad target once no hostile pawn remains and, within
	// DistantThreatCells of a colonist, a deficit for ActiveCombat and an
	// unsafe-threat hold like a hostile pawn, so the fight is planned under
	// a stopped clock and run under watched combat windows (the draft and
	// attack executors bind their reads by tick and are not safe under a
	// running colony window; live 2026-09-18, #246). A building further
	// out is watched like a distant animal: a map-gen hive in a cave the
	// colony never reaches held every window and every development goal
	// for good (#340).
	HostileBuilding
)

// EmergencyThreat is one native threat row. Animal and Distance (Chebyshev
// cells from the nearest living colonist) decide whether a hostile or hunting
// animal is close enough to be an emergency; either unknown keeps the hold.
// A HostileBuilding row is dead when destroyed and never downed; it carries
// its own native CAS token (SnapshotToken) for the attack order and its
// definition name.
type EmergencyThreat struct {
	ID           PawnID
	Kind         ThreatKind
	Dead, Downed domain.Fact[bool]
	Animal       domain.Fact[bool]
	Distance     domain.Fact[float64]
	// SnapshotToken, Definition and Cells are set on HostileBuilding rows
	// only; Cells is the building's occupied rect, the cells a ranged
	// defender needs a line of fire to (#327).
	SnapshotToken, Definition string
	Cells                     []domain.Cell
}

// Building reports whether the row is a hostile building rather than a pawn.
func (t EmergencyThreat) Building() bool { return t.Kind == HostileBuilding }

// DistantThreatCells is the nearest-colonist distance from which a hostile or
// hunting animal is watched rather than held: the native supervisor stops a
// running window for a hostile within the watch policy's hostile_within (20
// cells in serve) and for a predator hunt within 40 cells, both inside this
// band, so the approach re-enters the emergency before it can reach anyone.
// A humanlike or mechanoid threat holds at any distance: a raid is planned
// for from the map edge, not from twenty cells out. A hostile building
// (#246) goes nowhere, so the same band applies to it (#340).
const DistantThreatCells = 50.0

// DistantThreat reports a hostile or hunting animal, or a hostile building,
// known to be at least DistantThreatCells from every colonist.
func (t EmergencyThreat) DistantThreat() bool {
	animal, ak := t.Animal.Value()
	distance, dk := t.Distance.Value()
	return (t.Building() || ak && animal) && dk && distance >= DistantThreatCells
}

type EmergencyFacts struct {
	ColonistsComplete, ThreatsComplete domain.Fact[bool]
	Colonists                          []EmergencyPawn
	Threats                            []EmergencyThreat
}

// EmergencySnapshot owns its inputs and carries no mutation or authority API.
type EmergencySnapshot struct {
	current domain.GenerationSnapshot
	tick    domain.Tick
	facts   EmergencyFacts
	valid   bool
}
type EmergencyReason string

const (
	EmergencyUnknownFacts    EmergencyReason = "unknown_facts"
	EmergencyUnsafeThreat    EmergencyReason = "unsafe_threat"
	EmergencyCriticalMedical EmergencyReason = "critical_medical"
	EmergencyStaleFacts      EmergencyReason = "stale_facts"
)

type EmergencyHold struct {
	Reason EmergencyReason
	Pawn   PawnID
}
type EmergencyDecision struct {
	Clear bool
	Holds []EmergencyHold
}

func NewEmergencySnapshot(current domain.GenerationSnapshot, tick domain.Tick, facts EmergencyFacts) (EmergencySnapshot, error) {
	if err := current.Validate(); err != nil {
		return EmergencySnapshot{}, err
	}
	if tick < 0 || len(facts.Colonists) > 256 || len(facts.Threats) > 256 {
		return EmergencySnapshot{}, errors.New("invalid emergency tick or census size")
	}
	validID := func(id PawnID) bool {
		return utf8.ValidString(string(id)) && len(id) <= 256 && strings.TrimSpace(string(id)) != "" && !strings.ContainsRune(string(id), 0)
	}
	people := map[PawnID]bool{}
	for _, pawn := range facts.Colonists {
		if !validID(pawn.ID) || people[pawn.ID] {
			return EmergencySnapshot{}, errors.New("invalid or duplicate emergency pawn")
		}
		people[pawn.ID] = true
	}
	seen := map[struct {
		id   PawnID
		kind ThreatKind
	}]bool{}
	for _, threat := range facts.Threats {
		key := struct {
			id   PawnID
			kind ThreatKind
		}{threat.ID, threat.Kind}
		if !validID(threat.ID) || threat.Kind < Hostile || threat.Kind > HostileBuilding || seen[key] {
			return EmergencySnapshot{}, errors.New("invalid or duplicate emergency threat")
		}
		if threat.Building() != (threat.SnapshotToken != "" || threat.Definition != "" || len(threat.Cells) != 0) || threat.Building() && (!validID(PawnID(threat.SnapshotToken)) || !validID(PawnID(threat.Definition)) || len(threat.Cells) == 0) {
			return EmergencySnapshot{}, errors.New("hostile building threat requires its snapshot token, definition and occupied cells")
		}
		if distance, known := threat.Distance.Value(); known && (math.IsNaN(distance) || math.IsInf(distance, 0) || distance < 0) {
			return EmergencySnapshot{}, errors.New("invalid emergency threat distance")
		}
		seen[key] = true
	}
	facts.Colonists = append([]EmergencyPawn(nil), facts.Colonists...)
	facts.Threats = append([]EmergencyThreat(nil), facts.Threats...)
	for i := range facts.Threats {
		facts.Threats[i].Cells = append([]domain.Cell(nil), facts.Threats[i].Cells...)
	}
	return EmergencySnapshot{current: current, tick: tick, facts: facts, valid: true}, nil
}

// EvaluateEmergency clears only known complete, current facts. It does not decide
// how to treat, fight or advance time, and never grants execution authority.
func EvaluateEmergency(snapshot EmergencySnapshot, current domain.GenerationSnapshot, minimumTick domain.Tick) EmergencyDecision {
	if !snapshot.valid || current.Validate() != nil || !snapshot.current.Matches(current) || minimumTick < 0 || snapshot.tick < minimumTick {
		return EmergencyDecision{Holds: []EmergencyHold{{Reason: EmergencyStaleFacts}}}
	}
	holds := map[EmergencyHold]bool{}
	hold := func(reason EmergencyReason, pawn PawnID) { holds[EmergencyHold{reason, pawn}] = true }
	for _, complete := range []domain.Fact[bool]{snapshot.facts.ColonistsComplete, snapshot.facts.ThreatsComplete} {
		if yes, known := complete.Value(); !known || !yes {
			hold(EmergencyUnknownFacts, "")
		}
	}
	// A pawn may occur in several native categories; disagreeing known status is
	// uncertainty, not permission to choose whichever category appears safest.
	type status struct{ dead, downed domain.Fact[bool] }
	statuses := map[PawnID]status{}
	merge := func(id PawnID, dead, downed domain.Fact[bool]) {
		prior := statuses[id]
		combine := func(old, new domain.Fact[bool]) domain.Fact[bool] {
			a, ak := old.Value()
			b, bk := new.Value()
			if ak && bk && a != b {
				hold(EmergencyUnknownFacts, id)
			}
			if ak {
				return old
			}
			return new
		}
		statuses[id] = status{combine(prior.dead, dead), combine(prior.downed, downed)}
	}
	for _, pawn := range snapshot.facts.Colonists {
		merge(pawn.ID, pawn.Dead, pawn.Downed)
		dead, known := pawn.Dead.Value()
		if dead && known {
			continue
		}
		if !known {
			hold(EmergencyUnknownFacts, pawn.ID)
		}
		// Bleeding, or downed with a tend outstanding, is the emergency:
		// medical work the colony must do now. A colonist who only needs
		// tending (a chronic condition, a scratch a doctor or the pawn
		// tends natively while ticks pass) is the tend planner's patient,
		// not a reason to hold every dispatch until nobody can clear it
		// (#66). A colonist downed with nothing to tend (malnutrition,
		// exhaustion, a tended wound) is the rescue planner's patient: a
		// bed and ticks are the only care, and holding every other order
		// parked the colony until the watch expired (#304). Every health
		// fact still has to be known.
		if !allKnown(pawn.Downed, pawn.Bleeding, pawn.NeedsTend) {
			hold(EmergencyUnknownFacts, pawn.ID)
		} else if urgentPatient(pawn) {
			hold(EmergencyCriticalMedical, pawn.ID)
		}
	}
	for _, threat := range snapshot.facts.Threats {
		merge(threat.ID, threat.Dead, threat.Downed)
		dead, deadKnown := threat.Dead.Value()
		downed, downedKnown := threat.Downed.Value()
		if (deadKnown && dead) || (downedKnown && downed) {
			continue
		}
		if !deadKnown || !downedKnown {
			hold(EmergencyUnknownFacts, threat.ID)
		}
		// A distant animal is watched by the native supervisor's radius,
		// not held: no planner answers a manhunter or a hunting predator a
		// hundred cells out, and holding for one parked the clock for good;
		// nor is a hostile building that far out (#340).
		if (threat.Kind == Hostile || threat.Kind == HuntingPredator || threat.Kind == HostileBuilding) && !threat.DistantThreat() {
			hold(EmergencyUnsafeThreat, threat.ID)
		}
	}
	result := EmergencyDecision{Clear: len(holds) == 0, Holds: make([]EmergencyHold, 0, len(holds))}
	for h := range holds {
		result.Holds = append(result.Holds, h)
	}
	sort.Slice(result.Holds, func(i, j int) bool {
		a, b := result.Holds[i], result.Holds[j]
		if a.Reason != b.Reason {
			return a.Reason < b.Reason
		}
		return a.Pawn < b.Pawn
	})
	return result
}

func allKnown(facts ...domain.Fact[bool]) bool {
	for _, f := range facts {
		if _, known := f.Value(); !known {
			return false
		}
	}
	return true
}

// urgentPatient reports a living colonist whose care cannot wait for
// ordinary work: bleeding, or downed with a tend outstanding. Callers have
// established that every health fact is known.
func urgentPatient(pawn EmergencyPawn) bool {
	downed, _ := pawn.Downed.Value()
	bleeding, _ := pawn.Bleeding.Value()
	needsTend, _ := pawn.NeedsTend.Value()
	return bleeding || downed && needsTend
}
