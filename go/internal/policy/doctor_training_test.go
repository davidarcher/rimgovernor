package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func pegOp(kind SurgeryKind, recipe, body string, index int, added string, medicine float64) SurgeryOperation {
	op := SurgeryOperation{Recipe: domain.Known(recipe), PartDefName: domain.Known(body), PartIndex: domain.Known(index), Kind: kind,
		SuccessChance: domain.Known(0.95), EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true),
		Violation: domain.Known(kind != SurgeryRestore), Lethal: domain.Known(false), MedicineValue: domain.Known(medicine)}
	if added != "" {
		op.AddedPart = domain.Known(added)
	}
	return op
}

var (
	installPeg  = pegOp(SurgeryRestore, "InstallPegLeg", "Leg", 30, "", 20)
	removePeg   = pegOp(SurgeryAmputate, "RemoveBodyPart", "Leg", 30, "PegLeg", 10)
	cutHand     = pegOp(SurgeryHarvest, "RemoveBodyPart", "Hand", 12, "", 10)
	cutLeg      = pegOp(SurgeryHarvest, "RemoveBodyPart", "Leg", 31, "", 10)
	missingLeg  = MissingPart{PartDefName: domain.Known("Leg"), PartIndex: domain.Known(30)}
	missingLeg2 = MissingPart{PartDefName: domain.Known("Leg"), PartIndex: domain.Known(31)}
	pegPolicy   = PrisonerPolicy{ReleaseAfterDays: 30, FoodTargetDays: 5}
	pegFood     = domain.Known(20.0)
)

func pegPrisoner(id string, goodwill int, missing []MissingPart, ops ...SurgeryOperation) PrisonerFacts {
	row := prisonerRow(id, false, domain.PrisonerInteractionMaintain, 0, 1, weak)
	row.Operations, row.QueuedSurgeries, row.HarvestGoodwill = domain.Known(ops), domain.Known(0), domain.Known(goodwill)
	row.MissingParts, row.Withdrawal = domain.Known(missing), domain.Known(false)
	return row
}

func doctors(colonists int, medicine ...int) PrisonerColony {
	return PrisonerColony{Colonists: colonists, BestSkill: core.BestSkill, Medicine: medicine}
}

func TestTrainingValue(t *testing.T) {
	noDoctor := []SurgeryWant{{Pawn: "c", Reason: SurgeryNoDoctor}}
	for _, c := range []struct {
		name          string
		colony        PrisonerColony
		wants         []SurgeryWant
		perXP, cycles float64
	}{
		{"doctor below the floor", doctors(4, 3), nil, trainingXPValue, 9},
		{"one step from the floor", doctors(4, 9), nil, trainingXPValue, 2},
		{"floor met", doctors(4, 10, 2), nil, 0, 0},
		{"eight colonists want two", doctors(8, 10, 2), nil, trainingXPValue, maxSlotCycles},
		{"floor met but a restore waits on a doctor", doctors(4, 12), noDoctor, trainingCapXPValue, maxSlotCycles},
		{"no doctor at all", doctors(4), noDoctor, 0, 0},
	} {
		perXP, cycles := TrainingValue(c.colony, c.wants)
		if perXP != c.perXP || cycles != c.cycles {
			t.Fatalf("%s: %v x %v", c.name, perXP, cycles)
		}
	}
	if got := cycleXP("InstallPegLeg"); got != 5600 {
		t.Fatalf("peg-leg cycle XP %v, want vanilla's 5600", got)
	}
}

func TestSelectPegCycle(t *testing.T) {
	withdrawn := func(row PrisonerFacts) PrisonerFacts { row.Withdrawal = domain.Known(true); return row }
	recruiting := func(row PrisonerFacts) PrisonerFacts {
		row.CurrentInteraction = domain.Known(domain.PrisonerInteractionRecruit)
		return row
	}
	releaseDue := func(row PrisonerFacts) PrisonerFacts { row.HeldTicks = domain.Known(int64(40 * 60000)); return row }
	queued := func(row PrisonerFacts) PrisonerFacts { row.QueuedSurgeries = domain.Known(1); return row }
	for _, c := range []struct {
		name      string
		row       PrisonerFacts
		colony    PrisonerColony
		step      PegCycleStep
		recipe    string
		gain      float64
		cost      float64
		violation bool
	}{
		// Training: 5600 XP x 0.05 = 280 against three medicine (30), the
		// doctor's 3500 ticks (14) and the removal's goodwill.
		{"training installs into an open slot", pegPrisoner("p", 0, []MissingPart{missingLeg}, installPeg), doctors(4, 3), PegTraining, "InstallPegLeg", 280, 44, false},
		{"training removes the installed peg", pegPrisoner("p", 0, nil, removePeg), doctors(4, 3), PegTraining, "RemoveBodyPart", 280, 44, true},
		{"a faction's goodwill outweighs training", pegPrisoner("p", -70, []MissingPart{missingLeg}, installPeg), doctors(4, 3), 0, "", 0, 0, false},
		{"no training once the floor is met", pegPrisoner("p", 0, []MissingPart{missingLeg}, installPeg), doctors(4, 11), 0, "", 0, 0, false},
		{"cap-driven training above the floor", pegPrisoner("p", 0, []MissingPart{missingLeg}, installPeg), doctors(4, 11), 0, "", 0, 0, false},
		{"stops once the prisoner is recruited", recruiting(pegPrisoner("p", 0, []MissingPart{missingLeg}, installPeg)), doctors(4, 3), 0, "", 0, 0, false},
		{"stops once the prisoner is due for release", releaseDue(pegPrisoner("p", 0, nil, removePeg)), doctors(4, 3), 0, "", 0, 0, false},
		{"nothing while a surgery is queued", queued(pegPrisoner("p", 0, []MissingPart{missingLeg}, installPeg)), doctors(4, 3), 0, "", 0, 0, false},
		// No slot open: a natural hand's harvest (4 colonists x 5 mood x 20
		// = 400, plus a removal's medicine and labor) over the slot's cycles.
		{"a natural hand opens a slot", pegPrisoner("p", 0, nil, cutHand), doctors(4, 3), PegTraining, "RemoveBodyPart", 280, 44 + (400+10+8)/9.0, true},
		{"too few cycles left to pay for the cut", pegPrisoner("p", 0, nil, cutHand), doctors(7, 9), 0, "", 0, 0, false},
		{"never a second leg", pegPrisoner("p", 0, []MissingPart{missingLeg}, cutLeg), doctors(4, 3), 0, "", 0, 0, false},
		// Control: one peg (18 to remove) + feeding (25) + two reinstalls
		// (2 x (20 + 1.2 wood + 6)).
		{"withdrawal control removes the last peg", withdrawn(pegPrisoner("p", 0, []MissingPart{missingLeg}, removePeg)), doctors(4, 12), PegControl, "RemoveBodyPart", 400, 18 + 25 + 2*27.2, true},
		{"long-held control removes the last peg", pegPrisoner("p", 0, []MissingPart{missingLeg}, removePeg), doctors(4, 12), PegControl, "RemoveBodyPart", 200, 18 + 25 + 2*27.2, true},
		{"goodwill outweighs control", withdrawn(pegPrisoner("p", -70, []MissingPart{missingLeg}, removePeg)), doctors(4, 12), 0, "", 0, 0, false},
		{"no control while a natural leg stands", withdrawn(pegPrisoner("p", 0, nil, removePeg)), doctors(4, 12), 0, "", 0, 0, false},
		{"a recruit is never controlled", recruiting(withdrawn(pegPrisoner("p", 0, []MissingPart{missingLeg}, removePeg))), doctors(4, 12), 0, "", 0, 0, false},
		// Release: a legless prisoner gets its peg back first.
		{"reinstall before release", releaseDue(pegPrisoner("p", -70, []MissingPart{missingLeg, missingLeg2}, installPeg)), doctors(4, 12), PegReinstall, "InstallPegLeg", 0, 0, false},
		{"no reinstall while controlled", pegPrisoner("p", 0, []MissingPart{missingLeg, missingLeg2}, installPeg), doctors(4, 12), 0, "", 0, 0, false},
	} {
		wants := []SurgeryWant(nil)
		if c.name == "cap-driven training above the floor" {
			wants = []SurgeryWant{{Pawn: "c", Reason: SurgeryNoDoctor}}
			c.step, c.recipe, c.gain, c.cost = PegTraining, "InstallPegLeg", 112, 44
		}
		got, ok := SelectPegCycle(domain.Known([]PrisonerFacts{c.row}), domain.Known(c.colony), pegFood, pegPolicy, wants, nil)
		if ok != (c.step != 0) {
			t.Fatalf("%s: ok %v %+v", c.name, ok, got)
		}
		if ok && (got.Step != c.step || got.Recipe != c.recipe || got.Violation != c.violation || math.Abs(got.Gain-c.gain) > 1e-6 || math.Abs(got.Cost-c.cost) > 1e-6) {
			t.Fatalf("%s: got %+v", c.name, got)
		}
	}
}

func TestPrisonerUseHoldsLeglessRelease(t *testing.T) {
	row := pegPrisoner("p", 0, []MissingPart{missingLeg, missingLeg2})
	row.HeldTicks = domain.Known(int64(40 * 60000))
	if want, unknown := prisonerUse(row, core, pegFood, pegPolicy); want != "" || unknown {
		t.Fatalf("legless release: %q %v", want, unknown)
	}
	row.MissingParts = domain.Known([]MissingPart{missingLeg})
	if want, _ := prisonerUse(row, core, pegFood, pegPolicy); want != domain.PrisonerInteractionRelease {
		t.Fatalf("one peg back: %q", want)
	}
}
