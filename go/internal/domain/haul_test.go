package domain

import (
	"strings"
	"testing"
)

func TestHaulIntentAndClosedVariants(t *testing.T) {
	intent, err := NewHaul("pawn", "thing", "MealSimple")
	if err != nil || intent.Pawn() != "pawn" || intent.Thing() != "thing" || intent.Definition() != "MealSimple" {
		t.Fatal(intent, err)
	}
	action, err := NewHaulAction("haul", intent)
	if err != nil || action.ID() != "haul" || action.Kind() != HaulAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Haul(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("haul exposed building")
	}
	if _, ok := action.Tend(); ok {
		t.Fatal("haul exposed tend")
	}
	if _, err := NewHaulAction("haul", Haul{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewHaul(PawnID(invalid), "thing", "MealSimple"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewHaul("pawn", invalid, "MealSimple"); err == nil {
			t.Fatal("invalid thing accepted")
		}
		if _, err := NewHaul("pawn", "thing", invalid); err == nil {
			t.Fatal("invalid definition accepted")
		}
		if _, err := NewHaulAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestHaulPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewHaul("pawn", "thing", "MealSimple")
	action, _ := NewHaulAction("haul", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "haul")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("haul fabricated draft ownership")
	}
}

func TestHaulHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing haul handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, HaulAction, HaulAction}); err == nil {
		t.Fatal("duplicate haul handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
