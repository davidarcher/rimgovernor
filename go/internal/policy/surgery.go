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

// ElectiveFailureCap is the highest failure chance an elective upgrade
// (a better part on a healthy one, #1167) may carry; electives also need a
// hospital bed and no restore or cure pending colony-wide.
const ElectiveFailureCap = 0.05

// SurgeryContext is what ranking and electives need beyond the care read:
// each pawn's profile for role weights (absent weighs 1) and whether a
// medical bed stands (HospitalBedReady).
type SurgeryContext struct {
	Profiles    []PawnProfile
	HospitalBed bool
}

// SurgeryWantReason says why a patient's operation was not queued.
type SurgeryWantReason string

const (
	// SurgeryPartShort: no candidate recipe has its ingredients (the part
	// and medicine) on the map.
	SurgeryPartShort SurgeryWantReason = "surgery_part_short"
	// SurgeryNoDoctor: a stocked recipe exists but no eligible doctor
	// clears the failure cap.
	SurgeryNoDoctor SurgeryWantReason = "surgery_no_doctor"
	// SurgeryBedShort: an eligible doctor clears the cap with an ideal bed
	// and room but not with the colony's best medical bed (#1240); the
	// hospital planner builds a Bed ward for it (SurgeryBedShortPatients).
	SurgeryBedShort SurgeryWantReason = "surgery_bed_short"
)

// SurgeryChoice is one operation to queue as a SurgeryIntent.
type SurgeryChoice struct {
	Pawn   PawnID
	Recipe string
	Part   int
	Kind   SurgeryKind
	// Elective: an upgrade on a healthy part (#1167).
	Elective bool
	Value    float64
}

// SurgeryWant is an operation a patient needs that cannot be queued yet;
// part-short wants feed bill and trade demand (#1168).
type SurgeryWant struct {
	Pawn   PawnID
	Part   int
	Recipe string // the best recipe wanted
	Reason SurgeryWantReason
	// Options are every candidate recipe for the part, best first; Value
	// is the best one's rank, the same scale as SurgeryChoice.Value.
	Options []string
	Value   float64
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

// electivesAllowed: a medical bed stands and no living colonist has a
// served operation (restore, cure or chronic replacement) offered (#1167).
func electivesAllowed(rows []CarePawn, hospital bool) bool {
	if !hospital {
		return false
	}
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); dk && dead {
			continue
		}
		ops, known := pawn.Operations.Value()
		if !known {
			return false
		}
		for _, op := range ops {
			if served, known := servedSurgery(pawn, op); !known || served > 0 {
				return false
			}
		}
	}
	return true
}

// electiveUpgrade: an install on a healthy part whose part beats a natural
// one (bionic, archotech); implants and lesser parts are never electives.
func electiveUpgrade(op SurgeryOperation) bool {
	recipe, known := op.Recipe.Value()
	_, pk := op.PartIndex.Value()
	return op.Kind == SurgeryInstall && known && pk && partTier(recipe) > 1
}

// ElectiveSurgeryOwed: an elective upgrade could be queued now (stocked,
// within ElectiveFailureCap, a hospital bed, nothing served pending) on a
// living colonist without a queued bill. It keeps MaintainSurgery open;
// blocked electives never do.
func ElectiveSurgeryOwed(pawns domain.Fact[[]CarePawn], hospital domain.Fact[bool]) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if !electivesAllowed(rows, positive(hospital)) {
		return domain.Known(false)
	}
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); !dk || dead {
			continue
		}
		if queued, qk := pawn.QueuedSurgeries.Value(); !qk || queued > 0 {
			continue
		}
		ops, _ := pawn.Operations.Value()
		for _, op := range ops {
			if stocked, sk := op.IngredientsOnMap.Value(); electiveUpgrade(op) && sk && stocked && surgeryAcceptable(op, ElectiveFailureCap) {
				return domain.Known(true)
			}
		}
	}
	return domain.Known(false)
}

// HospitalBedReady: some humanlike, non-prisoner bed is flagged medical.
func HospitalBedReady(sleeping domain.Fact[SleepingObservation]) domain.Fact[bool] {
	obs, known := sleeping.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, bed := range obs.Beds {
		if positive(bed.Medical) && positive(bed.Humanlike) && !positive(bed.Prisoners) {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// SurgeryBillsQueued reports whether a living colonist or prisoner has a
// queued medical bill (#1238). A native doctor job carries the bill, so it
// is clock work until the operation completes or the bill ends; an unknown
// count is not work.
func SurgeryBillsQueued(pawns domain.Fact[[]CarePawn], prisoners domain.Fact[[]PrisonerFacts]) bool {
	queued := func(dead domain.Fact[bool], count domain.Fact[int]) bool {
		d, dk := dead.Value()
		n, nk := count.Value()
		return dk && !d && nk && n > 0
	}
	rows, _ := pawns.Value()
	for _, pawn := range rows {
		if queued(pawn.Dead, pawn.QueuedSurgeries) {
			return true
		}
	}
	held, _ := prisoners.Value()
	for _, row := range held {
		if queued(row.Dead, row.QueuedSurgeries) {
			return true
		}
	}
	return false
}

// SelectSurgery ranks every living patient's served operations. A patient
// with a queued bill or an open surgery action (inFlight) gets nothing new.
// Each part picks its best stocked recipe (bionic, then prosthetic, then
// peg) that some eligible doctor performs within the kind's failure cap;
// the patient queues its most valuable part, weighted by its role
// (UpgradeRoleWeight). When electives are allowed, healthy parts compete
// with their gain over the natural part, one elective per review.
func SelectSurgery(pawns domain.Fact[[]CarePawn], inFlight map[PawnID]bool, ctx SurgeryContext) SurgerySelection {
	var s SurgerySelection
	rows, _ := pawns.Value()
	electives := electivesAllowed(rows, ctx.HospitalBed)
	profiles := map[PawnID]PawnProfile{}
	for _, p := range ctx.Profiles {
		profiles[p.ID] = p
	}
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); !dk || dead || inFlight[pawn.ID] {
			continue
		}
		if queued, known := pawn.QueuedSurgeries.Value(); !known || queued > 0 {
			continue
		}
		profile, profiled := profiles[pawn.ID]
		ops, _ := pawn.Operations.Value()
		parts := map[int][]SurgeryOperation{}
		weights := map[int]float64{}
		elective := map[int]bool{}
		var order []int
		for _, op := range ops {
			part, pk := op.PartIndex.Value()
			if !pk {
				part = -1 // whole-body cure
			}
			weight, _ := servedSurgery(pawn, op)
			name, _ := op.PartDefName.Value()
			if weight <= 0 && electives && electiveUpgrade(op) {
				weight, elective[part] = partWeight(name), true
			}
			if _, rk := op.Recipe.Value(); !rk || weight <= 0 || !pk && op.Kind != SurgeryCure {
				continue
			}
			if profiled {
				weight *= UpgradeRoleWeight(profile, name)
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
			choice, want := selectPartSurgery(pawn.ID, part, weights[part], parts[part], elective[part])
			if choice == nil {
				if !elective[part] {
					s.Wants = append(s.Wants, want)
				}
			} else if best == nil || choice.Value > best.Value {
				best = choice
			}
		}
		if best != nil {
			s.Queue = append(s.Queue, *best)
		}
	}
	sort.SliceStable(s.Queue, func(i, j int) bool { return s.Queue[i].Value > s.Queue[j].Value })
	// The stock read is a bool, not a count: one elective per review, to
	// the pawn whose role gains most, so two bills never race for one part.
	queue, elective := s.Queue[:0], false
	for _, choice := range s.Queue {
		if choice.Elective {
			if elective {
				continue
			}
			elective = true
		}
		queue = append(queue, choice)
	}
	s.Queue = queue
	return s
}

// selectPartSurgery: an elective takes ElectiveFailureCap and is valued by
// its gain over the natural part, so any served operation outranks it.
func selectPartSurgery(pawn PawnID, part int, weight float64, ops []SurgeryOperation, elective bool) (*SurgeryChoice, SurgeryWant) {
	sort.SliceStable(ops, func(i, j int) bool { return opTier(ops[i]) > opTier(ops[j]) })
	top, _ := ops[0].Recipe.Value()
	topPart, _ := ops[0].PartDefName.Value()
	want := SurgeryWant{Pawn: pawn, Part: part, Recipe: top, Reason: SurgeryPartShort, Value: partTier(top) * partWeight(topPart)}
	for _, op := range ops {
		recipe, _ := op.Recipe.Value()
		want.Options = append(want.Options, recipe)
	}
	failureCap, natural := RestoreFailureCap, 0.0
	if elective {
		failureCap, natural = ElectiveFailureCap, 1
	}
	for _, op := range ops {
		if stocked, known := op.IngredientsOnMap.Value(); !known || !stocked {
			continue
		}
		if !surgeryAcceptable(op, failureCap) {
			if want.Reason != SurgeryBedShort {
				want.Reason = SurgeryNoDoctor
				if surgeryBedBlocked(op, failureCap) {
					want.Reason = SurgeryBedShort
				}
			}
			continue
		}
		recipe, _ := op.Recipe.Value()
		return &SurgeryChoice{Pawn: pawn, Recipe: recipe, Part: part, Kind: op.Kind, Elective: elective, Value: (opTier(op) - natural) * weight}, want
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

// surgeryBedBlocked: the operation fails the cap as observed but passes it
// with the doctor's ideal-bed chance, so the bed or room is the blocker.
func surgeryBedBlocked(op SurgeryOperation, failureCap float64) bool {
	ideal, known := op.DoctorSuccessChance.Value()
	if !known {
		return false
	}
	op.SuccessChance = domain.Known(ideal)
	return surgeryAcceptable(op, failureCap)
}

// SurgeryBedShortPatients are the living colonists whose served operation
// waits only on a better bed or room (#1240): the hospital planner's
// surgical demand, which needs a Bed, not a sleeping spot.
func SurgeryBedShortPatients(pawns domain.Fact[[]CarePawn]) []PawnID {
	var ids []PawnID
	seen := map[PawnID]bool{}
	for _, want := range SelectSurgery(pawns, nil, SurgeryContext{}).Wants {
		if want.Reason == SurgeryBedShort && !seen[want.Pawn] {
			seen[want.Pawn] = true
			ids = append(ids, want.Pawn)
		}
	}
	return ids
}

// partTier is the capacity a restore recipe's part gives back, relative to
// a natural part (1), read from vanilla's recipe naming.
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

// partWeight is how much of a colonist's capacities the part carries; the
// pawn's role scales it (UpgradeRoleWeight).
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
