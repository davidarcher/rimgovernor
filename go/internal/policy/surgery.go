package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainSurgery is the medical-operation goal (#1160). It restores missing
// or destroyed parts (#1164); later kinds (cure, amputation, electives) add
// their own candidates to SelectSurgery.
const MaintainSurgery GoalID = "MaintainSurgery"

// surgeryPriority keeps MaintainSurgery out of development arbitration: a
// queued bill is doctor work native assigns, not a reserved labor slot.
const surgeryPriority = 2

// RestoreFailureCap is the highest native failure chance a restore or a
// chronic cure (#1165) may carry for the best eligible doctor.
const RestoreFailureCap = 0.20

// SurgeryWantReason says why a patient's operation was not queued.
type SurgeryWantReason string

const (
	// SurgeryPartShort: no candidate recipe has its ingredients (the part
	// and medicine) on the map.
	SurgeryPartShort SurgeryWantReason = "surgery_part_short"
	// SurgeryNoDoctor: a stocked recipe exists but no eligible doctor
	// clears the failure cap.
	SurgeryNoDoctor SurgeryWantReason = "surgery_no_doctor"
)

// SurgeryChoice is one operation to queue as a SurgeryIntent.
type SurgeryChoice struct {
	Pawn   PawnID
	Recipe string
	Part   int
	Kind   SurgeryKind
	Value  float64
}

// SurgeryWant is an operation a patient needs that cannot be queued yet;
// part wants feed demand later (#1168).
type SurgeryWant struct {
	Pawn   PawnID
	Part   int
	Recipe string // the best recipe wanted
	Reason SurgeryWantReason
}

// SurgerySelection is one review's queue (at most one per patient, best
// value first) and its wants.
type SurgerySelection struct {
	Queue []SurgeryChoice
	Wants []SurgeryWant
}

// SurgeryRecovered: no living colonist has an operation the planner
// serves. A queued bill does not settle it; the health change does.
func SurgeryRecovered(pawns domain.Fact[[]CarePawn]) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); dk && dead {
			continue
		}
		ops, known := pawn.Operations.Value()
		if !known {
			return domain.Unknown[bool]()
		}
		for _, op := range ops {
			served, known := servedSurgery(pawn, op)
			if !known {
				return domain.Unknown[bool]()
			}
			if served > 0 {
				return domain.Known(false)
			}
		}
	}
	return domain.Known(true)
}

// servedSurgery is the capacity weight an operation gives back, zero when
// the planner does not serve it: a restore weighs its part; a cure recipe or
// a replacement part on a part carrying a chronic condition (#1165) weighs
// the condition. known is false when a cure's conditions are unread.
func servedSurgery(pawn CarePawn, op SurgeryOperation) (weight float64, known bool) {
	name, _ := op.PartDefName.Value()
	switch op.Kind {
	case SurgeryRestore:
		return partWeight(name), true
	case SurgeryCure:
	case SurgeryInstall:
		if recipe, _ := op.Recipe.Value(); partTier(recipe) <= 0.5 {
			return 0, true // an implant, not a replacement part
		}
	default:
		return 0, true
	}
	conditions, known := pawn.Conditions.Value()
	if !known {
		return 0, false
	}
	part, partKnown := op.PartIndex.Value()
	for _, c := range conditions {
		def, _ := c.DefName.Value()
		at, atKnown := c.PartIndex.Value()
		if atKnown == partKnown && (!atKnown || at == part) {
			weight = max(weight, chronicWeight[def])
		}
	}
	return weight, true
}

// chronicWeight is the capacity a chronic condition costs, which its cure
// or replacement gives back (#1165).
var chronicWeight = map[string]float64{
	"Cataract": 0.6, "Blindness": 0.6, "HearingLoss": 0.3,
	"BadBack": 0.8, "Frail": 0.8, "Asthma": 0.8, "ChemicalDamageModerate": 0.8,
	"Dementia": 1, "Alzheimers": 1, "HeartArteryBlockage": 1, "Cirrhosis": 1, "Carcinoma": 1, "ChemicalDamageSevere": 1,
}

// SelectSurgery ranks every living patient's served operations. A patient
// with a queued bill or an open surgery action (inFlight) gets nothing new.
// Each part picks its best stocked recipe (bionic, then prosthetic, then
// peg) that some eligible doctor performs within the kind's failure cap;
// the patient queues its most valuable part.
func SelectSurgery(pawns domain.Fact[[]CarePawn], inFlight map[PawnID]bool) SurgerySelection {
	var s SurgerySelection
	rows, _ := pawns.Value()
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); !dk || dead || inFlight[pawn.ID] {
			continue
		}
		if queued, known := pawn.QueuedSurgeries.Value(); !known || queued > 0 {
			continue
		}
		ops, _ := pawn.Operations.Value()
		parts := map[int][]SurgeryOperation{}
		weights := map[int]float64{}
		var order []int
		for _, op := range ops {
			part, pk := op.PartIndex.Value()
			if !pk {
				part = -1 // whole-body cure
			}
			weight, _ := servedSurgery(pawn, op)
			if _, rk := op.Recipe.Value(); !rk || weight <= 0 || !pk && op.Kind != SurgeryCure {
				continue
			}
			if parts[part] == nil {
				order = append(order, part)
			}
			parts[part] = append(parts[part], op)
			weights[part] = max(weights[part], weight)
		}
		sort.Ints(order)
		var best *SurgeryChoice
		for _, part := range order {
			choice, want := selectPartSurgery(pawn.ID, part, weights[part], parts[part])
			if choice == nil {
				s.Wants = append(s.Wants, want)
			} else if best == nil || choice.Value > best.Value {
				best = choice
			}
		}
		if best != nil {
			s.Queue = append(s.Queue, *best)
		}
	}
	sort.SliceStable(s.Queue, func(i, j int) bool { return s.Queue[i].Value > s.Queue[j].Value })
	return s
}

func selectPartSurgery(pawn PawnID, part int, weight float64, ops []SurgeryOperation) (*SurgeryChoice, SurgeryWant) {
	sort.SliceStable(ops, func(i, j int) bool { return opTier(ops[i]) > opTier(ops[j]) })
	top, _ := ops[0].Recipe.Value()
	want := SurgeryWant{Pawn: pawn, Part: part, Recipe: top, Reason: SurgeryPartShort}
	for _, op := range ops {
		if stocked, known := op.IngredientsOnMap.Value(); !known || !stocked {
			continue
		}
		want.Reason = SurgeryNoDoctor
		if !surgeryAcceptable(op, RestoreFailureCap) {
			continue
		}
		recipe, _ := op.Recipe.Value()
		return &SurgeryChoice{Pawn: pawn, Recipe: recipe, Part: part, Kind: op.Kind, Value: opTier(op) * weight}, want
	}
	return nil, want
}

// opTier: a cure leaves the natural part; a part recipe gives its tier.
func opTier(op SurgeryOperation) float64 {
	if op.Kind == SurgeryCure {
		return 1
	}
	recipe, _ := op.Recipe.Value()
	return partTier(recipe)
}

// surgeryAcceptable: some eligible doctor performs it within the failure
// cap, and it is neither a violation nor lethal. Unknown facts refuse.
func surgeryAcceptable(op SurgeryOperation, failureCap float64) bool {
	doctors, dk := op.EligibleDoctors.Value()
	chance, ck := op.SuccessChance.Value()
	violation, vk := op.Violation.Value()
	lethal, lk := op.Lethal.Value()
	return dk && ck && vk && lk && doctors > 0 && 1-chance <= failureCap+1e-9 && !violation && !lethal
}

// partTier is the capacity a restore recipe's part gives back, relative to
// a natural part, read from vanilla's recipe naming.
func partTier(recipe string) float64 {
	switch {
	case strings.Contains(recipe, "Archotech"):
		return 1.5
	case strings.Contains(recipe, "Bionic"):
		return 1.25
	case strings.Contains(recipe, "Natural"):
		return 1
	case strings.Contains(recipe, "Prosthetic"):
		return 0.85
	case strings.Contains(recipe, "Peg"), strings.Contains(recipe, "Wooden"), strings.Contains(recipe, "Denture"):
		return 0.6
	}
	return 0.5
}

// partWeight is how much of a colonist's capacities the part carries: the
// role weight is uniform until electives weight by role (#1167).
func partWeight(part string) float64 {
	for _, w := range []struct {
		part   string
		weight float64
	}{{"Leg", 1}, {"Arm", 1}, {"Shoulder", 1}, {"Heart", 1}, {"Lung", 1}, {"Kidney", 1}, {"Liver", 1}, {"Stomach", 1},
		{"Hand", 0.8}, {"Jaw", 0.7}, {"Foot", 0.6}, {"Eye", 0.6}, {"Ear", 0.3}, {"Nose", 0.1}} {
		if strings.Contains(part, w.part) {
			return w.weight
		}
	}
	return 0.5
}
