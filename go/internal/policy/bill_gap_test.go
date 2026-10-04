package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// selectGestation is SelectMechGestationBill read as selected-or-not.
func selectGestation(benches []ProductionBench, g MechGestation) (BillSelection, bool, error) {
	sel, gap, err := SelectMechGestationBill(benches, g)
	return sel, err == nil && gap == "", err
}

// Every refusal to queue a gestation names its cause.
func TestGestationGapNamesTheCause(t *testing.T) {
	yes := domain.Known(true)
	full := gestBench()
	for i := 0; i < 15; i++ {
		full.Bills = append(full.Bills, ExistingProductionBill{ID: "x", Recipe: "Make_Steel", Active: yes})
	}
	noRecipe := gestBench()
	noRecipe.Recipes = []ProductionRecipe{{Name: "MakeLifter", MechKind: "Lifter", Available: domain.Known(false)}}
	wasteful := gestation(gestMechanitor(6, 1, 0))
	wasteful.Gestators[0].WasteCount = domain.Known(int32(2))
	chargerless := gestation(gestMechanitor(6, 1, 0))
	chargerless.Chargers = nil
	cases := []struct {
		name    string
		benches []ProductionBench
		g       MechGestation
		want    BillGap
	}{
		{"waste", []ProductionBench{gestBench()}, wasteful, BillGapWaste},
		{"charger", []ProductionBench{gestBench()}, chargerless, BillGapNoCharger},
		{"in production", []ProductionBench{gestBench(ExistingProductionBill{ID: "b", Recipe: "MakeLifter", Active: domain.Known(false)})}, gestation(gestMechanitor(6, 1, 0)), BillGapInProduction},
		{"no recipe", []ProductionBench{noRecipe}, gestation(gestMechanitor(6, 1, 0)), BillGapNoRecipe},
		{"bench full", []ProductionBench{full}, gestation(gestMechanitor(6, 1, 0)), BillGapBenchFull},
		{"nothing affordable", []ProductionBench{gestBench()}, gestation(gestMechanitor(3, 3, 0)), BillGapNothingWanted},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sel, gap, err := SelectMechGestationBill(c.benches, c.g)
			if err != nil || gap != c.want || sel.Recipe != "" {
				t.Fatalf("gap %q selection %+v err %v, want %q", gap, sel, err, c.want)
			}
		})
	}
}

// Every refusal to queue a surgery part names its cause, for the
// highest-priority part that could not be served.
func TestSurgeryPartGapNamesTheCause(t *testing.T) {
	parts := legShort()
	full := partBench("fab", map[string]bool{"Make_BionicLeg": true, "Make_BionicEye": true})
	for i := 0; i < 15; i++ {
		full.Bills = append(full.Bills, ExistingProductionBill{ID: "x", Recipe: "Make_Other", Active: domain.Known(true)})
	}
	cases := []struct {
		name    string
		benches []ProductionBench
		parts   []SurgeryPart
		want    BillGap
	}{
		{"nothing wanted", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": true})}, nil, BillGapNothingWanted},
		{"in production", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": true, "Make_BionicEye": true}, "Make_BionicLeg", "Make_BionicEye")}, parts, BillGapInProduction},
		{"no recipe researched", []ProductionBench{partBench("fab", map[string]bool{"Make_BionicLeg": false})}, parts, BillGapNoRecipe},
		{"no bench", nil, parts, BillGapNoRecipe},
		{"bench full", []ProductionBench{full}, parts, BillGapBenchFull},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sel, gap := SelectSurgeryPartBill(c.benches, c.parts)
			if gap != c.want || sel.Recipe != "" {
				t.Fatalf("gap %q selection %+v, want %q", gap, sel, c.want)
			}
		})
	}
}
