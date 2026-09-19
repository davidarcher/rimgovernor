package domain

import "testing"

func TestClaimBuildingIdentity(t *testing.T) {
	if _, err := NewClaimBuilding("", "token"); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	if _, err := NewClaimBuilding("casket", ""); err == nil {
		t.Fatal("expected empty before token to be rejected")
	}
	c, err := NewClaimBuilding("casket", "token")
	if err != nil || c.Thing() != "casket" || c.BeforeToken() != "token" {
		t.Fatal(c, err)
	}
	action, err := NewClaimBuildingAction("a", c)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.ClaimBuilding(); !ok || got != c || action.Kind() != ClaimBuildingAction {
		t.Fatal("claim building action does not carry its value")
	}
	if _, err := NewClaimBuildingAction("a", ClaimBuilding{thing: "casket"}); err == nil {
		t.Fatal("expected a non-canonical value to be rejected")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}
