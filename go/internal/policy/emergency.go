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
	// InBed decides whether a bleeding colonist who is still on their feet
	// is anyone's patient: WorkGiver_Tend tends a humanlike only in a bed
	// and the ground tend needs a downed pawn (#618).
	InBed domain.Fact[bool]
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
	// Fogged is the native discovery fact: the pawn stands in fog the colony
	// has not explored. Unknown counts as discovered.
	Fogged domain.Fact[bool]
	// Passive is set on insects and hives only (#948): true when the thing
	// is dormant, or awake but not engaging the colony (no insect targets
	// anything of ours, no colonist inside the hive's boundary). A dormant
	// ruin hive makes jelly but neither spreads nor spawns; it is left
	// alone, never held for or attacked. Unknown counts as engaging.
	Passive domain.Fact[bool]
	// Position is a pawn threat's cell, when the census row carried one;
	// the safety overlay (#824) draws its reach around it.
	Position domain.Fact[domain.Cell]
	// Mortar is set on HostileBuilding rows only (#1148): the native def
	// fact, a turret whose verb fires mortar shells.
	Mortar bool
}

// Engaging reports a threat that is not known passive.
func (t EmergencyThreat) Engaging() bool {
	passive, known := t.Passive.Value()
	return !known || !passive
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
// known to be at least DistantThreatCells from every colonist. A crashed
// ship part is never distant (#1174): a defoliator's radius grows until it
// reaches the fields and a psychic droner's drone covers the whole map, so
// waiting for either to come closer loses the crops or the mood.
func (t EmergencyThreat) DistantThreat() bool {
	animal, ak := t.Animal.Value()
	distance, dk := t.Distance.Value()
	return (t.Building() && !ShipPart(t.Definition) || ak && animal) && dk && distance >= DistantThreatCells
}

// ShipPart reports a crashed ship part's definition (DefoliatorShipPart,
// PsychicDronerShipPart): a gunless hostile building that harms the colony
// from wherever it lands until destroyed (#1061, #1174).
func ShipPart(definition string) bool {
	return strings.Contains(definition, "ShipPart")
}

// Undiscovered reports a threat the colony has not found: a hostile standing
// in fog can neither reach a colonist nor be reached by one, so it is watched
// like a distant animal rather than held. The ancient-danger mechanoid sealed
// behind a shrine wall is the case that matters: holding for it parked the
// clock on unsafe_colony and deselected every development goal, including the
// ClearAncientShrine goal whose breach is the only thing that could ever
// clear it (#659, the #340 shape).
func (t EmergencyThreat) Undiscovered() bool {
	fogged, known := t.Fogged.Value()
	return known && fogged
}

// ThreatHolds reports whether one census row is an emergency the colony must
// answer before anything else: a live, standing hostile, hunting predator or
// hostile building it has discovered, that is not distant and that is not a
// passive insect or hive (#948). A nearby wild
// predator or downed animal is a watch row, never a hold.
func ThreatHolds(t EmergencyThreat) bool {
	if t.Kind != Hostile && t.Kind != HuntingPredator && t.Kind != HostileBuilding {
		return false
	}
	dead, dk := t.Dead.Value()
	downed, wk := t.Downed.Value()
	if dk && dead || wk && downed {
		return false
	}
	return t.Engaging() && !t.DistantThreat() && !t.Undiscovered()
}

type EmergencyFacts struct {
	ColonistsComplete domain.Fact[bool]
	Colonists         []EmergencyPawn
	Threats           []EmergencyThreat
	// PodsOpen is the open tick of the newest drop-pod arrival (#870) still
	// closed at the census tick, zero with none (#908): its raiders are in
	// their pods, so no census row names them yet.
	PodsOpen domain.Tick
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
	if tick < 0 {
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

// PodsPending reports a drop-pod raid on its way down (#908): an arrival
// whose pods have not opened by the census tick. It is a threat for the
// ActiveCombat goal and a combat window with nothing to acknowledge until
// the open, so the fight forms on the arrival.
func (s EmergencySnapshot) PodsPending() bool {
	return s.valid && s.facts.PodsOpen > 0 && s.facts.PodsOpen >= s.tick
}

// EvaluateEmergency clears only known complete, current facts. It does not decide
// how to treat, fight or advance time, and never grants execution authority.
func EvaluateEmergency(snapshot EmergencySnapshot, current domain.GenerationSnapshot, minimumTick domain.Tick) EmergencyDecision {
	if !snapshot.valid || current.Validate() != nil || !snapshot.current.Matches(current) || minimumTick < 0 || snapshot.tick < minimumTick {
		return EmergencyDecision{Holds: []EmergencyHold{{Reason: EmergencyStaleFacts}}}
	}
	holds := map[EmergencyHold]bool{}
	hold := func(reason EmergencyReason, pawn PawnID) { holds[EmergencyHold{reason, pawn}] = true }
	if yes, known := snapshot.facts.ColonistsComplete.Value(); !known || !yes {
		hold(EmergencyUnknownFacts, "")
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
		if AggressiveBreak(pawn) {
			hold(EmergencyUnsafeThreat, pawn.ID)
		}
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
		// A threat the colony has not discovered is nobody's deficit, and its
		// unread health is nothing to hold for either (#659).
		if threat.Undiscovered() {
			continue
		}
		if !deadKnown || !downedKnown {
			hold(EmergencyUnknownFacts, threat.ID)
		}
		// A distant animal is watched by the native supervisor's radius,
		// not held: no planner answers a manhunter or a hunting predator a
		// hundred cells out, and holding for one parked the clock for good;
		// nor is a hostile building that far out (#340).
		if ThreatHolds(threat) {
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
// urgentPatient is the CriticalMedical test: a bleeding colonist, or one
// downed with a tend outstanding. A bleeding colonist who is up and not in
// a bed is excluded (#618): no doctor can tend them (WorkGiver_Tend wants a
// humanlike in bed, the ground tend a downed pawn), and on a colony without
// a bed the hold suspended the very goal that would build one. Native sends
// them to a bed on its own when there is one, and the hold returns the
// moment they lie down. An unknown InBed keeps the hold, as before.
func urgentPatient(pawn EmergencyPawn) bool {
	downed, _ := pawn.Downed.Value()
	bleeding, _ := pawn.Bleeding.Value()
	needsTend, _ := pawn.NeedsTend.Value()
	if downed {
		return bleeding || needsTend
	}
	if inBed, known := pawn.InBed.Value(); known && !inBed {
		return false
	}
	return bleeding
}

// LosingImmunityRace projects an immunizable condition at its current
// rates: losing when severity reaches 1 no later than immunity does. Ties
// leave no safety margin; nonpositive immunity gain loses against
// progressing severity, and nonprogressing severity has no deadline.
// Known is false when any value is missing or not finite.
func LosingImmunityRace(c CareCondition) (losing, known bool) {
	severity, sk := c.Severity.Value()
	immunity, ik := c.Immunity.Value()
	sr, srk := c.SeverityPerDay.Value()
	ir, irk := c.ImmunityPerDay.Value()
	if !sk || !ik || !srk || !irk {
		return false, false
	}
	for _, v := range []float64{severity, immunity, sr, ir} {
		if math.IsNaN(v) || math.IsInf(v, 0) {
			return false, false
		}
	}
	if immunity >= 1 || sr <= 0 {
		return false, true
	}
	return ir <= 0 || (1-severity)/sr <= (1-immunity)/ir, true
}

// woundInfection is the limb infection an amputation removes (#1166);
// non-limb infections are out of scope.
const woundInfection = "WoundInfection"

// LifeSavingAmputations lists, for a living colonist, the amputation of the
// part carrying each wound infection that is losing its immunity race
// (#1166). The infected part itself is the smallest part whose removal
// takes the infection with it; a part native offers no amputation on (a
// torso, an organ) is not a limb and yields nothing. A projected loss is a
// projected death, so the operation needs no failure cap beyond a failure
// chance below certain death: vanilla's success chance must be known and
// positive, with an eligible doctor, no violation and no lethal outcome.
// Rows are ordered by part index.
func LifeSavingAmputations(pawn CarePawn) []domain.Surgery {
	if dead, known := pawn.Dead.Value(); !known || dead {
		return nil
	}
	conditions, ck := pawn.Conditions.Value()
	operations, ok := pawn.Operations.Value()
	if !ck || !ok {
		return nil
	}
	var out []domain.Surgery
	seen := map[int]bool{}
	for _, c := range conditions {
		name, nk := c.DefName.Value()
		part, pk := c.PartIndex.Value()
		if !nk || name != woundInfection || !pk || seen[part] {
			continue
		}
		if losing, known := LosingImmunityRace(c); !known || !losing {
			continue
		}
		for _, op := range operations {
			recipe, rk := op.Recipe.Value()
			at, ak := op.PartIndex.Value()
			success, sk := op.SuccessChance.Value()
			doctors, dk := op.EligibleDoctors.Value()
			violation, vk := op.Violation.Value()
			lethal, lk := op.Lethal.Value()
			if op.Kind != SurgeryAmputate || !rk || !ak || at != part || !sk || math.IsNaN(success) || success <= 0 || !dk || doctors <= 0 || !vk || violation || !lk || lethal {
				continue
			}
			s, err := domain.NewSurgery(domain.PawnID(pawn.ID), recipe, part, false)
			if err != nil {
				continue
			}
			seen[part] = true
			out = append(out, s)
			break
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Part() < out[j].Part() })
	return out
}
