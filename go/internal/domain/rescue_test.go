package domain

import (
	"strings"
	"testing"
)

func TestRescueIntentAndClosedVariants(t *testing.T) {
	intent, err := NewRescue("rescuer", "patient")
	if err != nil || intent.Rescuer() != "rescuer" || intent.Patient() != "patient" {
		t.Fatal(intent, err)
	}
	action, err := NewRescueAction("rescue", intent)
	if err != nil || action.ID() != "rescue" || action.Kind() != RescueAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Rescue(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Tend(); ok {
		t.Fatal("rescue exposed tend")
	}
	if _, ok := action.MeleeAttack(); ok {
		t.Fatal("rescue exposed melee")
	}
	if _, err := NewRescueAction("rescue", Rescue{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewRescue(PawnID(invalid), "patient"); err == nil {
			t.Fatal("invalid rescuer accepted")
		}
		if _, err := NewRescue("rescuer", PawnID(invalid)); err == nil {
			t.Fatal("invalid patient accepted")
		}
		if _, err := NewRescueAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewRescue("pawn", "pawn"); err == nil {
		t.Fatal("self rescue accepted")
	}
}

func TestRescuePlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewRescue("rescuer", "patient")
	action, _ := NewRescueAction("rescue", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "rescue")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("rescue fabricated draft ownership")
	}
}

func TestRescueHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction}); err == nil {
		t.Fatal("missing rescue handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RescueAction, RescueAction}); err == nil {
		t.Fatal("duplicate rescue handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
