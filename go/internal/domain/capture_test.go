package domain

import (
	"strings"
	"testing"
)

func TestCaptureIntentAndClosedVariants(t *testing.T) {
	intent, err := NewCapture("capturer", "patient")
	if err != nil || intent.Capturer() != "capturer" || intent.Patient() != "patient" {
		t.Fatal(intent, err)
	}
	action, err := NewCaptureAction("capture", intent)
	if err != nil || action.ID() != "capture" || action.Kind() != CaptureAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Capture(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Rescue(); ok {
		t.Fatal("capture exposed rescue")
	}
	if _, ok := action.MeleeAttack(); ok {
		t.Fatal("capture exposed melee")
	}
	if _, err := NewCaptureAction("capture", Capture{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewCapture(PawnID(invalid), "patient"); err == nil {
			t.Fatal("invalid capturer accepted")
		}
		if _, err := NewCapture("capturer", PawnID(invalid)); err == nil {
			t.Fatal("invalid patient accepted")
		}
		if _, err := NewCaptureAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewCapture("pawn", "pawn"); err == nil {
		t.Fatal("self capture accepted")
	}
}

func TestCapturePlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewCapture("capturer", "patient")
	action, _ := NewCaptureAction("capture", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "capture")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("capture fabricated draft ownership")
	}
}

func TestCaptureHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RescueAction}); err == nil {
		t.Fatal("missing capture handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RescueAction, CaptureAction, CaptureAction}); err == nil {
		t.Fatal("duplicate capture handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
