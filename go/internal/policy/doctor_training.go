package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Peg-leg cycling (#1236, epic #1160): MaintainSurgery installs and removes
// cheap wood parts (peg leg, wooden hand, wooden foot: one pool) on colony
// prisoners, one surgery at a time in the organ harvest's slot, for:
//
//   - Doctor training. A surgery teaches its surgeon surgeryXPPerWork
//     Medicine XP per work tick (SurgeryFlesh workSkillLearnFactor 16), so a
//     peg-leg cycle (install 1500 + removal 2000) gives 5600 XP. The XP is
//     worth trainingXPValue while fewer doctors than DoctorsWanted reach
//     DoctorTrainingFloor (carcinoma excision's minimum), trainingCapXPValue
//     while a restore waits on a doctor within the failure cap, else
//     nothing. A cycle costs three medicine (install two, removal one), the
//     doctor's time and the removal's goodwill (vanilla's harmful-surgery
//     -70 through HarvestGoodwill; the wood comes back, there is no mood
//     cost). With no wood slot open, a natural hand, foot or leg is
//     amputated to open one: its HarvestCost (vanilla treats it as a full
//     harvest) is spread over the cycles the slot is expected to support.
//     Anesthetize teaches nothing.
//   - Prisoner control. A prisoner with no legs is downed: no mental,
//     withdrawal or prison break. The last peg legs of a controlled
//     prisoner come off when the risk avoided outweighs the removals, the
//     hand-feeding warden labor and putting the pegs back later.
//   - Release. A legless prisoner no longer controlled gets a peg leg back
//     (vanilla cannot release a downed pawn; prisonerUse holds Release).
//
// Trainable: HarvestEligible, and the prisoner's use is not Release,
// Recruit, Convert or Enslave. Controlled: trainable, or in withdrawal,
// and never being recruited, converted or enslaved.

// DoctorTrainingFloor is the Medicine level the colony trains doctors to.
const DoctorTrainingFloor = 10

// Cycle model and silver prices (#1236).
//
// SilverPerLaborHour: one colonist hour (2500 ticks) of doctor or warden
// work. trainingXPValue prices a peg-leg cycle below the floor at 280
// silver: worth three medicine and the doctor's hour, not a faction's -70
// goodwill (350). trainingCapXPValue (112 a cycle) is what XP is worth
// above the floor while a restore waits on a better doctor.
// prisonerControlValue and withdrawalControlValue are the escape and break
// risk a legless prisoner avoids over its hold; wardenFeedCost the
// hand-feeding that hold takes.
const (
	SilverPerLaborHour     = 10.0
	surgeryXPPerWork       = 1.6
	woodRemovalWork        = 2000.0
	trainingXPValue        = 0.05
	trainingCapXPValue     = 0.02
	prisonerControlValue   = 200.0
	withdrawalControlValue = 400.0
	wardenFeedCost         = 25.0
	woodLogValue           = 1.2
	maxSlotCycles          = 10.0
	laborTicksPerHour      = 2500.0
)

// woodInstall maps a body part a wood part serves to its install recipe,
// and woodInstallWork each recipe's workAmount.
var (
	woodInstall     = map[string]string{"Leg": "InstallPegLeg", "Hand": "InstallWoodenHand", "Foot": "InstallWoodenFoot"}
	woodInstallWork = map[string]float64{"InstallPegLeg": 1500, "InstallWoodenHand": 1500, "InstallWoodenFoot": 1000}
	woodParts       = map[string]bool{"PegLeg": true, "WoodenHand": true, "WoodenFoot": true}
)

// DoctorsWanted is how many doctors should reach DoctorTrainingFloor: one,
// two from eight colonists.
func DoctorsWanted(colonists int) int {
	if colonists >= 8 {
		return 2
	}
	return 1
}

// medicineXPToFloor is vanilla's XP from level to DoctorTrainingFloor
// (XpRequiredToLevelUpFrom: 1000 x (level+1) below 10), passion ignored.
func medicineXPToFloor(level int) float64 {
	xp := 0.0
	for l := max(0, level); l < DoctorTrainingFloor; l++ {
		xp += 1000 * float64(l+1)
	}
	return xp
}

// cycleXP is one install and removal cycle's XP for an install recipe.
func cycleXP(install string) float64 {
	return surgeryXPPerWork * (woodInstallWork[install] + woodRemovalWork)
}

// TrainingValue is what one Medicine XP is worth and how many cycles a
// newly opened slot is expected to support: below the floor, until the
// best doctor under it reaches it; above it, while a restore waits on a
// doctor (surgery_no_doctor), maxSlotCycles.
func TrainingValue(c PrisonerColony, wants []SurgeryWant) (perXP, cycles float64) {
	at, below := 0, -1
	for _, level := range c.Medicine {
		if level >= DoctorTrainingFloor {
			at++
		} else {
			below = max(below, level)
		}
	}
	if at < DoctorsWanted(c.Colonists) && below >= 0 {
		return trainingXPValue, min(maxSlotCycles, math.Ceil(medicineXPToFloor(below)/cycleXP("InstallPegLeg")))
	}
	for _, want := range wants {
		if want.Reason == SurgeryNoDoctor && len(c.Medicine) > 0 {
			return trainingCapXPValue, maxSlotCycles
		}
	}
	return 0, 0
}

// PegCycleStep says why a peg-leg step is queued, in selection order; zero
// is a plain harvest or part recovery.
type PegCycleStep int

const (
	PegReinstall PegCycleStep = iota + 1
	PegControl
	PegTraining
)

// SelectPegCycle picks at most one peg-leg step under the harvest's
// in-flight rule, ranked by betterHarvest: a reinstall first, then control,
// then training; then the best gain less cost, then prisoner id. Unknown
// facts refuse.
func SelectPegCycle(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], food domain.Fact[float64], p PrisonerPolicy, wants []SurgeryWant, inFlight map[PawnID]bool) (OrganHarvest, bool) {
	rows, rk := prisoners.Value()
	c, ck := colony.Value()
	if !rk || !ck || surgeryInFlight(rows, inFlight) {
		return OrganHarvest{}, false
	}
	perXP, cycles := TrainingValue(c, wants)
	var best OrganHarvest
	found := false
	for _, row := range rows {
		for _, step := range pegCycleSteps(row, c, food, p, perXP, cycles) {
			if !found || betterHarvest(step, best) {
				best, found = step, true
			}
		}
	}
	return best, found
}

// PegCycleWanted reports whether a peg-leg step would be queued now;
// DetectRoutine holds MaintainSurgery open on it (#1236).
func PegCycleWanted(f RoutineFacts, p PrisonerPolicy) bool {
	_, ok := SelectPegCycle(f.Prisoners, f.PrisonerColony, f.FoodDays, p, SelectSurgery(f.MedicalPawns, nil, SurgeryContext{}).Wants, nil)
	return ok
}

// legless: every leg of a human body (two) is missing.
func (row PrisonerFacts) legless() bool {
	missing, _ := row.MissingParts.Value()
	return countMissing(missing, "Leg") >= 2
}

func countMissing(missing []MissingPart, part string) int {
	n := 0
	for _, m := range missing {
		if name, _ := m.PartDefName.Value(); name == part {
			n++
		}
	}
	return n
}

// pegCycleSteps are the row's acceptable steps: a reinstall, a control
// removal and a training step, each only when it applies and pays off.
func pegCycleSteps(row PrisonerFacts, c PrisonerColony, food domain.Fact[float64], p PrisonerPolicy, perXP, cycles float64) []OrganHarvest {
	intent, unknown := prisonerIntent(row, c, food, p)
	dead, dk := row.Dead.Value()
	ops, ok := row.Operations.Value()
	missing, mk := row.MissingParts.Value()
	goodwill, gk := row.HarvestGoodwill.Value()
	if unknown || !dk || dead || !ok || !mk || !gk {
		return nil
	}
	current, _ := row.CurrentInteraction.Value()
	planned := false
	for _, mode := range []domain.PrisonerInteractionMode{intent, current} {
		switch mode {
		case domain.PrisonerInteractionRecruit, domain.PrisonerInteractionConvert, domain.PrisonerInteractionEnslave:
			planned = true
		}
	}
	eligible, _ := row.HarvestEligible(c).Value()
	trainable := eligible && !planned && intent != domain.PrisonerInteractionRelease
	withdrawal, _ := row.Withdrawal.Value()
	controlled := !planned && (trainable || withdrawal)
	goodwillCost := SilverPerGoodwillPoint * float64(max(0, -goodwill))
	legsMissing := countMissing(missing, "Leg")

	var removals, installs, cuts []SurgeryOperation
	pegLegs := 0
	for _, op := range ops {
		body, _ := op.PartDefName.Value()
		recipe, _ := op.Recipe.Value()
		added, ak := op.AddedPart.Value()
		stocked, sk := op.IngredientsOnMap.Value()
		if !harvestAcceptable(op) {
			continue
		}
		switch {
		case op.Kind == SurgeryAmputate && ak && woodParts[added]:
			removals = append(removals, op)
			if added == "PegLeg" {
				pegLegs++
			}
		case op.Kind == SurgeryRestore && woodInstall[body] == recipe && sk && stocked:
			installs = append(installs, op)
		case (op.Kind == SurgeryHarvest || op.Kind == SurgeryAmputate) && !ak && woodInstall[body] != "" && !(body == "Leg" && legsMissing > 0):
			// #1232's rule: never a second leg.
			cuts = append(cuts, op)
		}
	}
	var out []OrganHarvest
	step := func(kind PegCycleStep, op SurgeryOperation, gain, cost float64) {
		recipe, _ := op.Recipe.Value()
		part, _ := op.PartIndex.Value()
		violation, _ := op.Violation.Value()
		out = append(out, OrganHarvest{Prisoner: row.Pawn, Recipe: recipe, Part: part, Gain: gain, Cost: cost, Violation: violation, Step: kind})
	}
	if legsMissing >= 2 && !controlled {
		for _, op := range installs {
			if body, _ := op.PartDefName.Value(); body == "Leg" {
				step(PegReinstall, op, 0, 0)
				break
			}
		}
	}
	if controlled && pegLegs > 0 && legsMissing+pegLegs >= 2 {
		value := prisonerControlValue
		if withdrawal {
			value = withdrawalControlValue
		}
		for _, op := range removals {
			if added, _ := op.AddedPart.Value(); added != "PegLeg" {
				continue
			}
			unit := medicineUnit(op, false)
			removal := unit + goodwillCost + laborCost(woodRemovalWork)
			reinstall := 2*unit + woodLogValue + laborCost(woodInstallWork["InstallPegLeg"])
			cost := float64(pegLegs)*removal + wardenFeedCost + 2*reinstall
			if value > cost {
				step(PegControl, op, value, cost)
			}
			break
		}
	}
	if !trainable || perXP <= 0 {
		return out
	}
	cycleCost := func(install string, unit float64) (gain, cost float64) {
		work := woodInstallWork[install] + woodRemovalWork
		return perXP * cycleXP(install), 3*unit + laborCost(work) + goodwillCost
	}
	switch {
	case len(removals) > 0:
		op := removals[0]
		added, _ := op.AddedPart.Value()
		if gain, cost := cycleCost("Install"+added, medicineUnit(op, false)); gain > cost {
			step(PegTraining, op, gain, cost)
		}
	case len(installs) > 0:
		var pick SurgeryOperation
		pickWork := -1.0
		for _, op := range installs {
			recipe, _ := op.Recipe.Value()
			if woodInstallWork[recipe] > pickWork {
				pick, pickWork = op, woodInstallWork[recipe]
			}
		}
		recipe, _ := pick.Recipe.Value()
		if gain, cost := cycleCost(recipe, medicineUnit(pick, true)); gain > cost {
			step(PegTraining, pick, gain, cost)
		}
	default:
		// No slot open: amputate a natural part, priced once as a harvest
		// and spread over the slot's cycles.
		for _, op := range cuts {
			body, _ := op.PartDefName.Value()
			harvest, ok := HarvestCost(c, goodwill, false)
			if !ok || cycles <= 0 {
				continue
			}
			gain, cost := cycleCost(woodInstall[body], medicineUnit(op, false))
			cost += (harvest + medicineUnit(op, false) + laborCost(woodRemovalWork)) / cycles
			if gain > cost {
				step(PegTraining, op, gain, cost)
			}
		}
	}
	return out
}

// medicineUnit is one medicine's value from an operation's medicine value:
// a wood install uses two, a removal one. Unknown reads as none.
func medicineUnit(op SurgeryOperation, install bool) float64 {
	v, known := op.MedicineValue.Value()
	if !known || !finite(v) || v < 0 {
		return 0
	}
	if install {
		return v / 2
	}
	return v
}

func laborCost(work float64) float64 { return SilverPerLaborHour * work / laborTicksPerHour }
