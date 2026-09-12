package domain

import (
	"strings"
	"testing"
)

func TestRangedAttackIntentAndClosedVariants(t *testing.T) {
	intent, err := NewRangedAttack("pawn", "hostile", "draft")
	if err != nil || intent.Pawn() != "pawn" || intent.Target() != "hostile" || intent.DraftAction() != "draft" {
		t.Fatal(intent, err)
	}
	action, err := NewRangedAttackAction("attack", intent)
	if err != nil || action.ID() != "attack" || action.Kind() != RangedAttackAction {
		t.Fatal(action, err)
	}
	if got, ok := action.RangedAttack(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.MeleeAttack(); ok {
		t.Fatal("ranged attack exposed melee")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("ranged attack exposed building")
	}
	if _, ok := action.OwnedDraft(); ok {
		t.Fatal("ranged attack exposed draft")
	}
	if _, err := NewRangedAttackAction("draft", intent); err == nil {
		t.Fatal("self prerequisite accepted")
	}
	if _, err := NewRangedAttackAction("attack", RangedAttack{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewRangedAttack(PawnID(invalid), "hostile", "draft"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewRangedAttack("pawn", PawnID(invalid), "draft"); err == nil {
			t.Fatal("invalid target accepted")
		}
		if _, err := NewRangedAttack("pawn", "hostile", ActionID(invalid)); err == nil {
			t.Fatal("invalid prerequisite accepted")
		}
		if _, err := NewRangedAttackAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewRangedAttack("pawn", "pawn", "draft"); err == nil {
		t.Fatal("self attack accepted")
	}
}

func TestRangedAttackPlanRequiresExactPrecedingOwnedDraft(t *testing.T) {
	draft, _ := NewOwnedDraft("pawn")
	draftAction, _ := NewOwnedDraftAction("draft", draft)
	otherDraft, _ := NewOwnedDraft("other")
	wrongPawn, _ := NewOwnedDraftAction("draft", otherDraft)
	otherID, _ := NewOwnedDraftAction("unrelated-draft", draft)
	intent, _ := NewRangedAttack("pawn", "hostile", "draft")
	attack, _ := NewRangedAttackAction("attack", intent)
	for _, actions := range [][]Action{{attack}, {attack, draftAction}, {wrongPawn, attack}, {otherID, attack}} {
		if _, err := NewPlan("plan", 1, actions); err == nil {
			t.Fatal("invalid ranged dependency accepted", actions)
		}
	}
	actions := []Action{draftAction, otherID, attack}
	plan, err := NewPlan("plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "attack")
	if err != nil || progress.Action() != attack || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("intent fabricated draft ownership")
	}
}

func TestRangedAttackHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RescueAction}); err == nil {
		t.Fatal("missing ranged attack handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, TendAction, RescueAction, RangedAttackAction, RangedAttackAction}); err == nil {
		t.Fatal("duplicate ranged attack handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
