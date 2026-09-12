package domain

import (
	"strings"
	"testing"
)

func TestTendIntentAndClosedVariants(t *testing.T) {
	intent, err := NewTend("doctor", "patient")
	if err != nil || intent.Doctor() != "doctor" || intent.Patient() != "patient" {
		t.Fatal(intent, err)
	}
	action, err := NewTendAction("tend", intent)
	if err != nil || action.ID() != "tend" || action.Kind() != TendAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Tend(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("tend exposed building")
	}
	if _, ok := action.OwnedDraft(); ok {
		t.Fatal("tend exposed draft")
	}
	if _, ok := action.MeleeAttack(); ok {
		t.Fatal("tend exposed melee")
	}
	if _, err := NewTendAction("tend", Tend{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewTend(PawnID(invalid), "patient"); err == nil {
			t.Fatal("invalid doctor accepted")
		}
		if _, err := NewTend("doctor", PawnID(invalid)); err == nil {
			t.Fatal("invalid patient accepted")
		}
		if _, err := NewTendAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewTend("pawn", "pawn"); err == nil {
		t.Fatal("self tend accepted")
	}
}

func TestTendPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewTend("doctor", "patient")
	action, _ := NewTendAction("tend", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "tend")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("tend fabricated draft ownership")
	}
}

func TestTendHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing tend handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, TendAction}); err == nil {
		t.Fatal("duplicate tend handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
