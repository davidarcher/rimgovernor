package domain

import (
	"strings"
	"testing"
)

func TestCleanIntentAndClosedVariants(t *testing.T) {
	intent, err := NewClean("pawn", "filth", Cell{X: 3, Z: 4})
	if err != nil || intent.Pawn() != "pawn" || intent.Filth() != "filth" || intent.Cell() != (Cell{X: 3, Z: 4}) {
		t.Fatal(intent, err)
	}
	action, err := NewCleanAction("clean", intent)
	if err != nil || action.ID() != "clean" || action.Kind() != CleanAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Clean(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Haul(); ok {
		t.Fatal("clean exposed haul")
	}
	if _, ok := action.Repair(); ok {
		t.Fatal("clean exposed repair")
	}
	if _, err := NewCleanAction("clean", Clean{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewClean(PawnID(invalid), "filth", Cell{X: 1, Z: 1}); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewClean("pawn", invalid, Cell{X: 1, Z: 1}); err == nil {
			t.Fatal("invalid filth accepted")
		}
		if _, err := NewCleanAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	for _, cell := range []Cell{{X: -1, Z: 0}, {X: 0, Z: -1}} {
		if _, err := NewClean("pawn", "filth", cell); err == nil {
			t.Fatal("negative cell accepted")
		}
	}
}

func TestCleanPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewClean("pawn", "filth", Cell{X: 1, Z: 1})
	action, _ := NewCleanAction("clean", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "clean")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("clean fabricated draft ownership")
	}
}

func TestCleanHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction}); err == nil {
		t.Fatal("missing clean handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, CleanAction, CleanAction}); err == nil {
		t.Fatal("duplicate clean handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
