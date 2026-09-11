package domain

import (
	"strings"
	"testing"
)

func TestMeleeAttackIntentAndClosedVariants(t *testing.T) {
	intent, err := NewMeleeAttack("pawn", "hostile", "draft")
	if err != nil || intent.Pawn() != "pawn" || intent.Target() != "hostile" || intent.DraftAction() != "draft" {
		t.Fatal(intent, err)
	}
	action, err := NewMeleeAttackAction("attack", intent)
	if err != nil || action.ID() != "attack" || action.Kind() != MeleeAttackAction {
		t.Fatal(action, err)
	}
	if got, ok := action.MeleeAttack(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("melee exposed building")
	}
	if _, ok := action.OwnedDraft(); ok {
		t.Fatal("melee exposed draft")
	}
	if _, err := NewMeleeAttackAction("draft", intent); err == nil {
		t.Fatal("self prerequisite accepted")
	}
	if _, err := NewMeleeAttackAction("attack", MeleeAttack{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewMeleeAttack(PawnID(invalid), "hostile", "draft"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewMeleeAttack("pawn", PawnID(invalid), "draft"); err == nil {
			t.Fatal("invalid target accepted")
		}
		if _, err := NewMeleeAttack("pawn", "hostile", ActionID(invalid)); err == nil {
			t.Fatal("invalid prerequisite accepted")
		}
		if _, err := NewMeleeAttackAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewMeleeAttack("pawn", "pawn", "draft"); err == nil {
		t.Fatal("self attack accepted")
	}
}

func TestMeleePlanRequiresExactPrecedingOwnedDraft(t *testing.T) {
	draft, _ := NewOwnedDraft("pawn")
	draftAction, _ := NewOwnedDraftAction("draft", draft)
	otherDraft, _ := NewOwnedDraft("other")
	wrongPawn, _ := NewOwnedDraftAction("draft", otherDraft)
	otherID, _ := NewOwnedDraftAction("unrelated-draft", draft)
	building, _ := NewBuilding("Wall", Cell{}, North, "")
	wrongKind, _ := NewBuildingAction("draft", building)
	intent, _ := NewMeleeAttack("pawn", "hostile", "draft")
	attack, _ := NewMeleeAttackAction("attack", intent)
	for _, actions := range [][]Action{{attack}, {attack, draftAction}, {wrongPawn, attack}, {otherID, attack}, {wrongKind, attack}, {draftAction, attack, attack}} {
		if _, err := NewPlan("plan", 1, actions); err == nil {
			t.Fatal("invalid melee dependency accepted", actions)
		}
	}
	mixed := attack
	mixed.draft = draft
	if _, err := NewPlan("plan", 1, []Action{draftAction, mixed}); err == nil {
		t.Fatal("mixed melee variant accepted")
	}
	mixedDraft := draftAction
	mixedDraft.melee = intent
	if _, err := NewPlan("plan", 1, []Action{mixedDraft, attack}); err == nil {
		t.Fatal("mixed draft variant accepted")
	}
	actions := []Action{draftAction, otherID, attack}
	plan, err := NewPlan("plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	actions[2] = Action{}
	if plan.Actions()[2] != attack {
		t.Fatal("plan retained mutable action slice")
	}
	progress, err := NewProgress(plan, "attack")
	if err != nil || progress.Action() != attack || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("intent fabricated draft ownership")
	}
}

func TestMeleeHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction}); err == nil {
		t.Fatal("missing melee handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, MeleeAttackAction}); err == nil {
		t.Fatal("duplicate melee handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
