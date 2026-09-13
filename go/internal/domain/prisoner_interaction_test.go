package domain

import (
	"strings"
	"testing"
)

func TestPrisonerInteractionIntentAndClosedVariants(t *testing.T) {
	recruit, err := NewPrisonerInteraction("prisoner", PrisonerInteractionRecruit)
	if err != nil || recruit.Pawn() != "prisoner" || recruit.Interaction() != PrisonerInteractionRecruit {
		t.Fatal(recruit, err)
	}
	action, err := NewPrisonerInteractionAction("prisoner-interaction-1", recruit)
	if err != nil || action.ID() != "prisoner-interaction-1" || action.Kind() != PrisonerInteractionAction {
		t.Fatal(action, err)
	}
	if got, ok := action.PrisonerInteraction(); !ok || got != recruit {
		t.Fatal(got, ok)
	}
	if _, ok := action.Husbandry(); ok {
		t.Fatal("prisoner interaction exposed husbandry")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("prisoner interaction exposed building")
	}

	maintain, err := NewPrisonerInteraction("prisoner", PrisonerInteractionMaintain)
	if err != nil || maintain.Interaction() != PrisonerInteractionMaintain {
		t.Fatal(maintain, err)
	}

	if _, err := NewPrisonerInteractionAction("prisoner-interaction-1", PrisonerInteraction{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	if _, err := NewPrisonerInteraction("prisoner", PrisonerInteractionMode("release")); err == nil {
		t.Fatal("invalid interaction accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewPrisonerInteraction(PawnID(invalid), PrisonerInteractionRecruit); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewPrisonerInteractionAction(ActionID(invalid), recruit); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestPrisonerInteractionPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	maintain, _ := NewPrisonerInteraction("prisoner", PrisonerInteractionMaintain)
	action, _ := NewPrisonerInteractionAction("prisoner-interaction-1", maintain)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "prisoner-interaction-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestPrisonerInteractionHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing prisoner interaction handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, PrisonerInteractionAction, PrisonerInteractionAction}); err == nil {
		t.Fatal("duplicate prisoner interaction handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
