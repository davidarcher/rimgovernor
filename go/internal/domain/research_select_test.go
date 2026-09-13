package domain

import (
	"strings"
	"testing"
)

func TestResearchSelectIntentAndClosedVariants(t *testing.T) {
	intent, err := NewResearchSelect("ProjectDef")
	if err != nil || intent.Project() != "ProjectDef" {
		t.Fatal(intent, err)
	}
	action, err := NewResearchSelectAction("research", intent)
	if err != nil || action.ID() != "research" || action.Kind() != ResearchSelectAction {
		t.Fatal(action, err)
	}
	if got, ok := action.ResearchSelect(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("research select exposed building")
	}
	if _, ok := action.GearReplace(); ok {
		t.Fatal("research select exposed gear replace")
	}
	if _, err := NewResearchSelectAction("research", ResearchSelect{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewResearchSelect(invalid); err == nil {
			t.Fatal("invalid project accepted")
		}
		if _, err := NewResearchSelectAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestResearchSelectPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewResearchSelect("ProjectDef")
	action, _ := NewResearchSelectAction("research", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "research")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestResearchSelectHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing research select handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, ResearchSelectAction, ResearchSelectAction}); err == nil {
		t.Fatal("duplicate research select handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
