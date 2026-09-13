package domain

import (
	"strings"
	"testing"
)

func TestGearReplaceIntentAndClosedVariants(t *testing.T) {
	intent, err := NewGearReplace("pawn", "thing", "Parka")
	if err != nil || intent.Pawn() != "pawn" || intent.Thing() != "thing" || intent.Definition() != "Parka" {
		t.Fatal(intent, err)
	}
	action, err := NewGearReplaceAction("gear", intent)
	if err != nil || action.ID() != "gear" || action.Kind() != GearReplaceAction {
		t.Fatal(action, err)
	}
	if got, ok := action.GearReplace(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("gear replace exposed building")
	}
	if _, ok := action.Equip(); ok {
		t.Fatal("gear replace exposed equip")
	}
	if _, err := NewGearReplaceAction("gear", GearReplace{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewGearReplace(PawnID(invalid), "thing", "Parka"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewGearReplace("pawn", invalid, "Parka"); err == nil {
			t.Fatal("invalid thing accepted")
		}
		if _, err := NewGearReplace("pawn", "thing", invalid); err == nil {
			t.Fatal("invalid definition accepted")
		}
		if _, err := NewGearReplaceAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestGearReplacePlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewGearReplace("pawn", "thing", "Parka")
	action, _ := NewGearReplaceAction("gear", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "gear")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestGearReplaceHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing gear replace handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, GearReplaceAction, GearReplaceAction}); err == nil {
		t.Fatal("duplicate gear replace handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
