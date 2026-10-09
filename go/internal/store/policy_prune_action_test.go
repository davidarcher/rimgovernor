package store

import (
	"context"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A policy prune persists its database and canonical ids.
func TestPolicyPruneActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	var actions []domain.Action
	for i, db := range []domain.PolicyDatabase{domain.OutfitPolicies, domain.DrugPolicies, domain.FoodPolicies, domain.ReadingPolicies, domain.AllowedAreas} {
		value, err := domain.NewPolicyPrune(db, []string{"Policy_9", "Policy_2"})
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewPolicyPruneAction(domain.ActionID([]string{"o", "d", "f", "r", "a"}[i]), value)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan("prune-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "prune-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != len(actions) {
		t.Fatal(got)
	}
	for i, a := range got {
		v, ok := a.PolicyPrune()
		want, _ := actions[i].PolicyPrune()
		if !ok || v != want || !slices.Equal(v.IDs(), []string{"Policy_2", "Policy_9"}) {
			t.Fatal(i, v, want)
		}
	}
}
