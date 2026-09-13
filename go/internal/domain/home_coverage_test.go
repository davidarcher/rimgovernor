package domain

import (
	"strings"
	"testing"
)

func TestHomeCoverageIntentAndClosedVariants(t *testing.T) {
	coverage, err := NewHomeCoverage("building-1", "shape-token")
	if err != nil || coverage.Target() != "building-1" || coverage.Shape() != "shape-token" {
		t.Fatal(coverage, err)
	}
	action, err := NewHomeCoverageAction("home-coverage-1", coverage)
	if err != nil || action.ID() != "home-coverage-1" || action.Kind() != HomeCoverageAction {
		t.Fatal(action, err)
	}
	if got, ok := action.HomeCoverage(); !ok || got != coverage {
		t.Fatal(got, ok)
	}
	if _, ok := action.Husbandry(); ok {
		t.Fatal("home coverage exposed husbandry")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("home coverage exposed building")
	}

	if _, err := NewHomeCoverageAction("home-coverage-1", HomeCoverage{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewHomeCoverage(invalid, "shape-token"); err == nil {
			t.Fatal("invalid target accepted")
		}
		if _, err := NewHomeCoverage("building-1", invalid); err == nil {
			t.Fatal("invalid shape accepted")
		}
		if _, err := NewHomeCoverageAction(ActionID(invalid), coverage); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
}

func TestHomeCoveragePlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	coverage, _ := NewHomeCoverage("building-1", "shape-token")
	action, _ := NewHomeCoverageAction("home-coverage-1", coverage)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "home-coverage-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestHomeCoverageHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing home coverage handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, HomeCoverageAction, HomeCoverageAction}); err == nil {
		t.Fatal("duplicate home coverage handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
