package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainSurgery is the medical-operation goal. It restores missing
// or destroyed parts; later kinds (cure, amputation, electives) add
// their own candidates to SelectSurgery.
const MaintainSurgery ConcernID = "MaintainSurgery"

// surgeryPriority keeps MaintainSurgery out of development arbitration: a
// queued bill is doctor work native assigns, not a reserved labor slot.
const surgeryPriority = 2

// RestoreFailureCap is the highest native failure chance a restore or a
// chronic cure may carry for the best eligible doctor.
const RestoreFailureCap = 0.20

// ElectiveFailureCap is the highest failure chance an elective upgrade
// (a better part on a healthy one) may carry; electives also need a
// hospital bed and no restore or cure pending colony-wide.
const ElectiveFailureCap = 0.05

// SurgeryContext is what ranking and electives need beyond the care read:
// each pawn's profile for role weights (absent weighs 1) and whether a
// medical bed stands (HospitalBedReady).
type SurgeryContext struct {
	Profiles    []PawnProfile
	HospitalBed bool
	// Elective gates electives by each colonist's remaining personal share;
	// the zero value is ungated.
	Elective ElectiveShare
}

// ElectiveShareSlack is the stateless hysteresis of the elective gate: an
// elective is affordable while its part's market value is within
// (1+slack) x the colonist's remaining share. Electives are only offered once
// the part is on the map, so the part is already bought or fabricated and
// wealth jitter around the price must not strand it.
const ElectiveShareSlack = 0.1

// ElectiveShare prices electives against personal shares. Of is the
// colonist's share (observation.ColonyProjection.PersonalShareOf); nil gates
// nothing. Items price the installed part; a part with no price is not
// affordable while Of is set, as an unknown share is not.
type ElectiveShare struct {
	Items ItemFacts
	Of    func(PawnID) PersonalShare
}

// affordable is whether the pawn's remaining share covers the elective's
// installed part, within ElectiveShareSlack.
func (g ElectiveShare) affordable(pawn PawnID, op SurgeryOperation) bool {
	if g.Of == nil {
		return true
	}
	price, err := g.Items.MarketValue(op.Item)
	if op.Item == "" || err != nil {
		return false
	}
	return g.Of(pawn).Allows(price / (1 + ElectiveShareSlack))
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
	// and room but not with the colony's best medical bed; the
	// hospital planner builds a Bed ward for it (SurgeryBedShortPatients).
	SurgeryBedShort SurgeryWantReason = "surgery_bed_short"
)

// SurgeryChoice is one operation to queue as a medical ProductionBillIntent.
type SurgeryChoice struct {
	Pawn   PawnID
	Recipe string
	// Item is the part item the recipe installs ("" when it installs none).
	Item Resource
	Part int
	Kind SurgeryKind
	// Elective: an upgrade on a healthy part.
	Elective bool
	Value    float64
}

// SurgeryWant is an operation a patient needs that cannot be queued yet;
// part-short wants feed bill and trade demand.
type SurgeryWant struct {
	Pawn   PawnID
	Part   int
	Recipe string // the best recipe wanted
	Reason SurgeryWantReason
	// Options are every candidate recipe for the part, best first; Value
	// is the best one's rank, the same scale as SurgeryChoice.Value.
	Options []string
	// Items are the part items of the options that install one, in order.
	Items []Resource
	Value float64
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
// a replacement part on a part carrying a chronic condition weighs
// the condition. known is false when a cure's conditions are unread.
func servedSurgery(pawn CarePawn, op SurgeryOperation) (weight float64, known bool) {
	name, _ := op.PartDefName.Value()
	switch op.Kind {
	case SurgeryRestore:
		return partWeight(name), true
	case SurgeryCure:
	case SurgeryInstall:
		if op.Tier == 0 {
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
// or replacement gives back.
var chronicWeight = map[string]float64{
	"Cataract": 0.6, "Blindness": 0.6, "HearingLoss": 0.3,
	"BadBack": 0.8, "Frail": 0.8, "Asthma": 0.8, "ChemicalDamageModerate": 0.8,
	"Dementia": 1, "Alzheimers": 1, "HeartArteryBlockage": 1, "Cirrhosis": 1, "Carcinoma": 1, "ChemicalDamageSevere": 1,
}

// electivesAllowed: a medical bed stands and no living colonist has a
// surgery bill queued or a served operation (restore, cure or chronic
// replacement) offered.
func electivesAllowed(rows []CarePawn, hospital bool) bool {
	if !hospital {
		return false
	}
	for _, pawn := range rows {
		if dead, dk := pawn.Dead.Value(); dk && dead {
			continue
		}
		// The stock read is a bool: one surgery bill anywhere holds
		// electives, or a later review books the same part twice.
		if queued, qk := pawn.QueuedSurgeries.Value(); !qk || queued > 0 {
			return false
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
	_, known := op.Recipe.Value()
	_, pk := op.PartIndex.Value()
	return op.Kind == SurgeryInstall && known && pk && op.Tier > 1
}

// ElectiveSurgeryOwed: an elective upgrade could be queued now (stocked,
// within ElectiveFailureCap, a hospital bed, nothing served pending, the
// part within the colonist's remaining share) on a living colonist without a
// queued bill. It keeps MaintainSurgery open; blocked or unaffordable
// electives never do.
//
// A chosen elective whose part is missing (ChosenElective) also holds it open
// while a usable bench can fabricate the part: the part bill is the
// work. fabricable is FabricableParts; nil fabricates nothing.
func ElectiveSurgeryOwed(pawns domain.Fact[[]CarePawn], ctx SurgeryContext, fabricable map[Resource]bool) domain.Fact[bool] {
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if !electivesAllowed(rows, ctx.HospitalBed) {
		return domain.Known(false)
	}
	gate := ctx.Elective
	if want, ok := ChosenElective(pawns, ctx); ok && len(fabricableItems(want.Items, fabricable)) > 0 {
		return domain.Known(true)
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
			if stocked, sk := op.IngredientsOnMap.Value(); electiveUpgrade(op) && sk && stocked && surgeryAcceptable(op, ElectiveFailureCap) && gate.affordable(pawn.ID, op) {
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
// queued medical bill. A native doctor job carries the bill, so it
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
// (UpgradeRoleWeight). When electives are allowed, healthy parts whose
// installed part fits the colonist's remaining share (ctx.Elective) compete
// with their gain over the natural part, one elective per review colony-wide.
// Served operations never pass the share gate.
func SelectSurgery(pawns domain.Fact[[]CarePawn], inFlight map[PawnID]bool, ctx SurgeryContext) SurgerySelection {
	var s SurgerySelection
	rows, _ := pawns.Value()
	electives := electivesAllowed(rows, ctx.HospitalBed)
	profiles := map[PawnID]PawnProfile{}
	for _, p := range ctx.Profiles {
		profiles[p.ID] = p
	}
	for _, pawn := range rows {
		if pawn.QuestProtected {
			continue
		}
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
			if weight <= 0 && electives && electiveUpgrade(op) && ctx.Elective.affordable(pawn.ID, op) {
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
	sort.SliceStable(s.Queue, func(i, j int) bool {
		if a, b := s.Queue[i], s.Queue[j]; a.Value != b.Value {
			return a.Value > b.Value
		}
		return s.Queue[i].Pawn < s.Queue[j].Pawn
	})
	// The stock read is a bool, not a count: one elective per review, to
	// the pawn whose role gains most (ties by pawn id), so two bills never
	// race for one part.
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
	want := SurgeryWant{Pawn: pawn, Part: part, Recipe: top, Reason: SurgeryPartShort, Value: ops[0].Tier * partWeight(topPart)}
	for _, op := range ops {
		recipe, _ := op.Recipe.Value()
		want.Options = append(want.Options, recipe)
		if op.Item != "" {
			want.Items = append(want.Items, op.Item)
		}
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
		return &SurgeryChoice{Pawn: pawn, Recipe: recipe, Item: op.Item, Part: part, Kind: op.Kind, Elective: elective, Value: (opTier(op) - natural) * weight}, want
	}
	return nil, want
}

// opTier: a cure leaves the natural part; a part recipe gives its tier.
func opTier(op SurgeryOperation) float64 {
	if op.Kind == SurgeryCure {
		return 1
	}
	return op.Tier
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
// waits only on a better bed or room: the hospital planner's
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
