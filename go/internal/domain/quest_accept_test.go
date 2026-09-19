package domain

import (
	"strings"
	"testing"
)

func TestQuestAcceptIntentAndClosedVariants(t *testing.T) {
	accept, err := NewQuestAccept("quest-1", "pawn-1", 2)
	if err != nil || accept.Quest() != "quest-1" || accept.AccepterPawn() != "pawn-1" || accept.RewardChoice() != 2 {
		t.Fatal(accept, err)
	}
	action, err := NewQuestAcceptAction("quest-accept-1", accept)
	if err != nil || action.ID() != "quest-accept-1" || action.Kind() != QuestAcceptAction {
		t.Fatal(action, err)
	}
	if got, ok := action.QuestAccept(); !ok || got != accept {
		t.Fatal(got, ok)
	}
	if _, ok := action.PrisonerInteraction(); ok {
		t.Fatal("quest accept exposed prisoner interaction")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("quest accept exposed building")
	}

	noChoice, err := NewQuestAccept("quest-1", "", -1)
	if err != nil || noChoice.AccepterPawn() != "" || noChoice.RewardChoice() != -1 {
		t.Fatal(noChoice, err)
	}

	if _, err := NewQuestAccept("quest-1", "pawn-1", -2); err == nil {
		t.Fatal("invalid reward choice accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewQuestAccept(QuestID(invalid), "", -1); err == nil {
			t.Fatal("invalid quest identity accepted")
		}
		if _, err := NewQuestAcceptAction(ActionID(invalid), accept); err == nil {
			t.Fatal("invalid action identity accepted")
		}
	}
	// An empty accepter pawn is valid: it means "quest does not need one",
	// unlike an empty quest or action identity, which is always invalid.
	for _, invalid := range []string{" ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewQuestAccept("quest-1", PawnID(invalid), -1); err == nil {
			t.Fatal("invalid accepter pawn accepted")
		}
	}
}

func TestQuestAcceptPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	accept, _ := NewQuestAccept("quest-1", "", -1)
	action, _ := NewQuestAcceptAction("quest-accept-1", accept)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "quest-accept-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestQuestAcceptHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing quest accept handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, QuestAcceptAction, QuestAcceptAction}); err == nil {
		t.Fatal("duplicate quest accept handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
