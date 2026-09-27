package domain

import "testing"

func TestClaimBuildingIdentity(t *testing.T) {
	if _, err := NewClaimBuilding(""); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	c, err := NewClaimBuilding("casket")
	if err != nil || c.Thing() != "casket" {
		t.Fatal(c, err)
	}
	action, err := NewClaimBuildingAction("a", c)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.ClaimBuilding(); !ok || got != c || action.Kind() != ClaimBuildingAction {
		t.Fatal("claim building action does not carry its value")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}
