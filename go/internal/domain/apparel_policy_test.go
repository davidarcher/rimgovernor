package domain

import (
	"math"
	"testing"
)

func TestApparelPolicyCanonicalAndBounded(t *testing.T) {
	s := ApparelPolicySpec{Pawn: "pawn", Token: "cas", Name: "RimGovernor worker", Definitions: []string{"Shirt", "Hat"}, MinHP: .51, MaxHP: 1, MaxQuality: 6}
	v, err := NewApparelPolicy(s)
	if err != nil {
		t.Fatal(err)
	}
	s.Definitions[0] = "Changed"
	if v.Spec().Definitions[1] != "Shirt" {
		t.Fatal("mutable policy")
	}
	for _, change := range []func(*ApparelPolicySpec){func(s *ApparelPolicySpec) { s.Token = "" }, func(s *ApparelPolicySpec) { s.MinHP = math.NaN() }, func(s *ApparelPolicySpec) { s.MaxHP = math.Inf(1) }, func(s *ApparelPolicySpec) { s.MinQuality = 7 }, func(s *ApparelPolicySpec) { s.Definitions = []string{"Hat", "Hat"} }} {
		bad := v.Spec()
		change(&bad)
		if _, err := NewApparelPolicy(bad); err == nil {
			t.Fatalf("accepted %+v", bad)
		}
	}
}
