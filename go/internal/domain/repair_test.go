package domain

import (
	"strings"
	"testing"
)

func TestRepairIntentAndClosedVariants(t *testing.T) {
	intent, err := NewRepair("pawn", "wall", Cell{X: 3, Z: 4})
	if err != nil || intent.Pawn() != "pawn" || intent.Structure() != "wall" || intent.Cell() != (Cell{X: 3, Z: 4}) {
		t.Fatal(intent, err)
	}
	action, err := NewRepairAction("repair", intent)
	if err != nil || action.ID() != "repair" || action.Kind() != RepairAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Repair(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Haul(); ok {
		t.Fatal("repair exposed haul")
	}
	if _, ok := action.Rescue(); ok {
		t.Fatal("repair exposed rescue")
	}
	if _, err := NewRepairAction("repair", Repair{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewRepair(PawnID(invalid), "wall", Cell{X: 1, Z: 1}); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewRepair("pawn", invalid, Cell{X: 1, Z: 1}); err == nil {
			t.Fatal("invalid structure accepted")
		}
		if _, err := NewRepairAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	for _, cell := range []Cell{{X: -1, Z: 0}, {X: 0, Z: -1}} {
		if _, err := NewRepair("pawn", "wall", cell); err == nil {
			t.Fatal("negative cell accepted")
		}
	}
}

func TestRepairPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewRepair("pawn", "wall", Cell{X: 1, Z: 1})
	action, _ := NewRepairAction("repair", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "repair")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("repair fabricated draft ownership")
	}
}

func TestRepairHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction}); err == nil {
		t.Fatal("missing repair handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RepairAction, RepairAction}); err == nil {
		t.Fatal("duplicate repair handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
