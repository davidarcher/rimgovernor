package domain

import "testing"

func TestMoveBuildingIdentity(t *testing.T) {
	for _, bad := range []struct {
		thing, def string
		cell       Cell
		rot        Rotation
	}{{"", "Bed", Cell{1, 1}, North}, {"bed1", "", Cell{1, 1}, North}, {"bed1", "Bed", Cell{-1, 1}, North}, {"bed1", "Bed", Cell{1, 1}, "all"}} {
		if _, err := NewMoveBuilding(bad.thing, bad.def, bad.cell, bad.rot); err == nil {
			t.Fatalf("accepted %+v", bad)
		}
	}
	m, err := NewMoveBuilding("Thing_Bed1", "Bed", Cell{4, 7}, East)
	if err != nil || m.Thing() != "Thing_Bed1" || m.Definition() != "Bed" || m.Cell() != (Cell{4, 7}) || m.Rotation() != East {
		t.Fatal(m, err)
	}
	action, err := NewMoveBuildingAction("a", m)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.MoveBuilding(); !ok || got != m || action.Kind() != MoveBuildingAction {
		t.Fatal("move action does not carry its value")
	}
	if _, err := NewMoveBuildingAction("a", MoveBuilding{thing: "x"}); err == nil {
		t.Fatal("expected a non-canonical value to be rejected")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}
