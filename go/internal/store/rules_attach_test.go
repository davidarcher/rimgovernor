package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rulesAttachPlan(t *testing.T, plan, action string, rules []domain.Rule) domain.PlanSpec {
	t.Helper()
	attach, err := domain.NewRulesAttach(rules, 2500)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewRulesAttachAction(domain.ActionID(action), attach)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan(domain.PlanID(plan), 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return spec
}

func TestRulesAttachActionRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "rules.sqlite"))
	rule := domain.Rule{ID: "hunt-chain", Trigger: domain.RulePreyKilled, Predicates: []domain.RulePredicate{domain.RuleActorUndrafted}, Action: domain.RuleGiveJob, Job: "Hunt", Target: domain.RuleNearestDesignatedPrey, Radius: 80, PredatorMarginCells: 25}
	for name, rules := range map[string][]domain.Rule{"attach": {rule}, "clear": nil} {
		spec := rulesAttachPlan(t, name, name+"-0", rules)
		if err := s.CreatePlan(ctx, spec); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		got, err := s.LoadPlan(ctx, spec.ID())
		if err != nil || got.Spec.Actions()[0] != spec.Actions()[0] {
			t.Fatalf("%s: %+v %v", name, got, err)
		}
	}
}

func TestRulesAttachOnlyPlans(t *testing.T) {
	if !rulesAttachOnly(rulesAttachPlan(t, "p", "a", nil)) {
		t.Fatal("a rules attach plan is not exempt")
	}
	strip, _ := domain.NewStrip("Corpse_Human12")
	a, _ := domain.NewStripAction("strip-0", strip)
	other, _ := domain.NewPlan("q", 1, []domain.Action{a})
	if rulesAttachOnly(other) {
		t.Fatal("a strip plan is exempt")
	}
}
