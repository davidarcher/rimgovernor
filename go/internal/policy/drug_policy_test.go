package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func drugPawn(id string, age float64, traits []PawnTrait, chemicals ...ChemicalState) WorkPawn {
	return WorkPawn{ID: PawnID(id), Age: domain.Known(age), Traits: domain.Known(traits),
		PolicyInputs: domain.Known(PawnPolicyInputs{DrugPolicy: "DrugPolicy_1", Chemicals: chemicals})}
}

func joyDrugs(t *testing.T, pawn WorkPawn) []string {
	t.Helper()
	entries, ok := DrugEntries(pawn)
	if !ok {
		t.Fatal("unknown")
	}
	var defs []string
	for _, e := range entries {
		if !e.Joy || e.Addiction || e.Scheduled || e.TakeToInventory != 0 {
			t.Fatal(e)
		}
		defs = append(defs, e.Drug)
	}
	return defs
}

func TestDrugEntriesPerInput(t *testing.T) {
	if got := joyDrugs(t, drugPawn("adult", 30, nil)); len(got) != 3 || got[0] != "Beer" || got[1] != "SmokeleafJoint" || got[2] != "PsychiteTea" {
		t.Fatal("plain adult", got)
	}
	if got := joyDrugs(t, drugPawn("child", 9, nil)); len(got) != 0 {
		t.Fatal("child", got)
	}
	for _, degree := range []int{-1, 1, 2} {
		if got := joyDrugs(t, drugPawn("trait", 30, []PawnTrait{{Name: "DrugDesire", Degree: degree}})); len(got) != 0 {
			t.Fatal("DrugDesire", degree, got)
		}
	}
	tolerant := ChemicalState{Chemical: "Alcohol", Tolerance: domain.Known(0.6)}
	if got := joyDrugs(t, drugPawn("tolerant", 30, nil, tolerant)); len(got) != 2 || got[0] != "SmokeleafJoint" {
		t.Fatal("high tolerance", got)
	}
	low := ChemicalState{Chemical: "Alcohol", Tolerance: domain.Known(0.2)}
	if got := joyDrugs(t, drugPawn("low", 30, nil, low)); len(got) != 3 {
		t.Fatal("low tolerance", got)
	}
	addicted := ChemicalState{Chemical: "Psychite", Addiction: domain.Known(0.3)}
	if got := joyDrugs(t, drugPawn("addict", 30, nil, addicted)); len(got) != 2 || got[1] != "SmokeleafJoint" {
		t.Fatal("addiction", got)
	}
	unknown := drugPawn("unknown", 30, nil)
	unknown.Traits = domain.Unknown[[]PawnTrait]()
	if _, ok := DrugEntries(unknown); ok {
		t.Fatal("unknown traits planned")
	}
}

func TestDrugPolicyChanges(t *testing.T) {
	adult, child := drugPawn("adult", 30, nil), drugPawn("child", 9, nil)
	names := []OwnedName{{Pawn: "adult", Short: "Bob"}, {Pawn: "child", Short: "Tim"}}
	changes := DrugPolicyChanges([]WorkPawn{adult, child}, names, nil)
	if len(changes) != 2 || changes[0].Write == nil || changes[0].Write.Name() != "Bob" || len(changes[0].Write.Entries()) != 3 || changes[0].Assign == nil {
		t.Fatal(changes)
	}
	if changes[1].Write == nil || len(changes[1].Write.Entries()) != 0 || changes[1].Assign == nil {
		t.Fatal(changes[1])
	}
	// The held policy already carrying the contents owes nothing, in any
	// observed order.
	held := []DrugPolicyEntry{
		{ID: "DrugPolicy_1", Label: "Bob", Entries: []domain.DrugPolicyEntry{joyEntry("SmokeleafJoint"), joyEntry("Beer"), joyEntry("PsychiteTea")}},
		{ID: "DrugPolicy_2", Label: "Tim"},
	}
	if got := DrugPolicyChanges([]WorkPawn{adult}, names, held); len(got) != 0 {
		t.Fatal(got)
	}
	// The child's policy exists but is not held: assign only.
	if got := DrugPolicyChanges([]WorkPawn{child}, names, held); len(got) != 1 || got[0].Write != nil || got[0].Assign == nil {
		t.Fatal(got)
	}
	// A shared short name waits for the rename.
	if got := DrugPolicyChanges([]WorkPawn{adult}, append(names, OwnedName{Pawn: "other", Short: "bob"}), nil); len(got) != 0 {
		t.Fatal(got)
	}
}
