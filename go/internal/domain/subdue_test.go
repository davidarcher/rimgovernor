package domain

import (
	"strings"
	"testing"
)

func TestSubdueIntentAndClosedVariants(t *testing.T) {
	intent, err := NewSubdue("pawn", "hostile", "draft")
	if err != nil || intent.Pawn() != "pawn" || intent.Target() != "hostile" || intent.DraftAction() != "draft" {
		t.Fatal(intent, err)
	}
	action, err := NewSubdueAction("attack", intent)
	if err != nil || action.ID() != "attack" || action.Kind() != SubdueAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Subdue(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("melee exposed building")
	}
	if _, ok := action.OwnedDraft(); ok {
		t.Fatal("melee exposed draft")
	}
	if _, err := NewSubdueAction("draft", intent); err == nil {
		t.Fatal("self prerequisite accepted")
	}
	if _, err := NewSubdueAction("attack", Subdue{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewSubdue(PawnID(invalid), "hostile", "draft"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewSubdue("pawn", PawnID(invalid), "draft"); err == nil {
			t.Fatal("invalid target accepted")
		}
		if _, err := NewSubdue("pawn", "hostile", ActionID(invalid)); err == nil {
			t.Fatal("invalid prerequisite accepted")
		}
		if _, err := NewSubdueAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewSubdue("pawn", "pawn", "draft"); err == nil {
		t.Fatal("self attack accepted")
	}
}

func TestSubduePlanRequiresExactPrecedingOwnedDraft(t *testing.T) {
	draft, _ := NewOwnedDraft("pawn")
	draftAction, _ := NewOwnedDraftAction("draft", draft)
	otherDraft, _ := NewOwnedDraft("other")
	wrongPawn, _ := NewOwnedDraftAction("draft", otherDraft)
	otherID, _ := NewOwnedDraftAction("unrelated-draft", draft)
	building, _ := NewBuilding("Wall", Cell{}, North, "")
	wrongKind, _ := NewBuildingAction("draft", building, TierExpand)
	intent, _ := NewSubdue("pawn", "hostile", "draft")
	attack, _ := NewSubdueAction("attack", intent)
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
	mixedDraft.subdue = intent
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
}
