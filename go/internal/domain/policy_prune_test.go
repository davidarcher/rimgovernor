package domain

import (
	"slices"
	"testing"
)

func TestPolicyPruneCanonicalAndRefusals(t *testing.T) {
	a, err := NewPolicyPrune(OutfitPolicies, []string{"ApparelPolicy_3", "ApparelPolicy_1"})
	if err != nil {
		t.Fatal(err)
	}
	b, _ := NewPolicyPrune(OutfitPolicies, []string{"ApparelPolicy_1", "ApparelPolicy_3"})
	if a != b || !slices.Equal(a.IDs(), []string{"ApparelPolicy_1", "ApparelPolicy_3"}) || a.Database() != OutfitPolicies {
		t.Fatal(a, b)
	}
	for _, bad := range []struct {
		db  PolicyDatabase
		ids []string
	}{
		{"", []string{"x"}}, {"zone", []string{"x"}}, {DrugPolicies, nil}, {FoodPolicies, []string{"x", "x"}}, {AllowedAreas, []string{" "}},
	} {
		if _, err := NewPolicyPrune(bad.db, bad.ids); err == nil {
			t.Fatal(bad)
		}
	}
	action, err := NewPolicyPruneAction("prune", a)
	if v, ok := action.PolicyPrune(); err != nil || !ok || v != a || action.Kind() != PolicyPruneAction {
		t.Fatal(err)
	}
	if _, err := NewPolicyPruneAction("prune", PolicyPrune{}); err == nil {
		t.Fatal("zero prune accepted")
	}
}
