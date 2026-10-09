package domain

import "testing"

func TestGatheringRequiresDefAndOrganizer(t *testing.T) {
	for _, c := range []struct {
		def       string
		organizer PawnID
	}{{"", "Pawn_1"}, {"Party", ""}, {" ", "Pawn_1"}, {"Party", " "}} {
		if _, err := NewGathering(c.def, c.organizer); err == nil {
			t.Fatalf("accepted %q/%q", c.def, c.organizer)
		}
	}
	g, err := NewGathering("Party", "Pawn_1")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewGatheringAction("party", g)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := a.Gathering(); !ok || got.Def() != "Party" || got.Organizer() != "Pawn_1" {
		t.Fatalf("gathering = %+v, %v", got, ok)
	}
	if _, err = NewPlan("plan", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
	if _, err = NewGatheringAction("", g); err == nil {
		t.Fatal("empty action id accepted")
	}
	if _, err = NewGatheringAction("party", Gathering{}); err == nil {
		t.Fatal("zero gathering accepted")
	}
}
