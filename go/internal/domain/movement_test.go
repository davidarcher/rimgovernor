package domain

import (
	"strings"
	"testing"
)

func TestMovementIntentAndClosedVariants(t *testing.T) {
	intent, err := NewMovement("pawn", Cell{X: 1, Z: 2}, "draft")
	if err != nil || intent.Pawn() != "pawn" || intent.Destination() != (Cell{X: 1, Z: 2}) || intent.DraftAction() != "draft" {
		t.Fatal(intent, err)
	}
	action, err := NewMovementAction("move", intent)
	if err != nil || action.ID() != "move" || action.Kind() != MovementAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Movement(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.RangedAttack(); ok {
		t.Fatal("movement exposed ranged attack")
	}
	if _, ok := action.MeleeAttack(); ok {
		t.Fatal("movement exposed melee")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("movement exposed building")
	}
	if _, ok := action.OwnedDraft(); ok {
		t.Fatal("movement exposed draft")
	}
	if _, err := NewMovementAction("draft", intent); err == nil {
		t.Fatal("self prerequisite accepted")
	}
	if _, err := NewMovementAction("move", Movement{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewMovement(PawnID(invalid), Cell{X: 1, Z: 2}, "draft"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewMovement("pawn", Cell{X: 1, Z: 2}, ActionID(invalid)); err == nil {
			t.Fatal("invalid prerequisite accepted")
		}
		if _, err := NewMovementAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	for _, negative := range []Cell{{X: -1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: -1}} {
		if _, err := NewMovement("pawn", negative, "draft"); err == nil {
			t.Fatal("negative destination accepted", negative)
		}
	}
}

func TestMovementPlanRequiresExactPrecedingOwnedDraft(t *testing.T) {
	draft, _ := NewOwnedDraft("pawn")
	draftAction, _ := NewOwnedDraftAction("draft", draft)
	otherDraft, _ := NewOwnedDraft("other")
	wrongPawn, _ := NewOwnedDraftAction("draft", otherDraft)
	otherID, _ := NewOwnedDraftAction("unrelated-draft", draft)
	intent, _ := NewMovement("pawn", Cell{X: 1, Z: 2}, "draft")
	move, _ := NewMovementAction("move", intent)
	for _, actions := range [][]Action{{move}, {move, draftAction}, {wrongPawn, move}, {otherID, move}} {
		if _, err := NewPlan("plan", 1, actions); err == nil {
			t.Fatal("invalid movement dependency accepted", actions)
		}
	}
	actions := []Action{draftAction, otherID, move}
	plan, err := NewPlan("plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "move")
	if err != nil || progress.Action() != move || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("intent fabricated draft ownership")
	}
}

func TestMovementHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, RangedAttackAction, SupplyAllowAction, TendAction, RescueAction}); err == nil {
		t.Fatal("missing movement handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, RangedAttackAction, SupplyAllowAction, TendAction, RescueAction, MovementAction, MovementAction}); err == nil {
		t.Fatal("duplicate movement handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
