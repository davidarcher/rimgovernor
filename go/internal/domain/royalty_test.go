package domain

import "testing"

func TestRoyaltyValidates(t *testing.T) {
	if _, err := NewRoyalty("", "Empire", RoyaltyChoosePermit, "P"); err == nil {
		t.Fatal("accepted no pawn")
	}
	if _, err := NewRoyalty("p", "", RoyaltyChoosePermit, "P"); err == nil {
		t.Fatal("accepted no faction")
	}
	if _, err := NewRoyalty("p", "Empire", "abdicate", "P"); err == nil {
		t.Fatal("accepted an unknown verb")
	}
	if _, err := NewRoyalty("p", "Empire", RoyaltyChoosePermit, ""); err == nil {
		t.Fatal("accepted no permit")
	}
}

func TestRoyaltyActionSurvivesPlanCanonicalisation(t *testing.T) {
	value, err := NewRoyalty("p", "Empire", RoyaltyChoosePermit, "P")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewRoyaltyAction("a", value)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := NewPlan("plan", 1, []Action{a})
	if err != nil {
		t.Fatal(err)
	}
	got, ok := plan.Actions()[0].Royalty()
	if !ok || got != value {
		t.Fatal(got, ok)
	}
}
