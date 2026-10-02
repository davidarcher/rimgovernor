package domain

import "testing"

func TestFloorRemovalAction(t *testing.T) {
	f, err := NewFloorRemoval("WoodPlankFloor", Cell{X: 4, Z: 7})
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewFloorRemovalAction("remove-floor-1", f)
	if err != nil {
		t.Fatal(err)
	}
	got, ok := a.FloorRemoval()
	if !ok || a.Kind() != FloorRemovalAction || got.Definition() != "WoodPlankFloor" || got.Cell() != (Cell{X: 4, Z: 7}) {
		t.Fatalf("floor removal = %+v %v", got, ok)
	}
	if _, ok := (Action{}).FloorRemoval(); ok {
		t.Fatal("zero action reported a floor removal")
	}
	if _, err := NewFloorRemoval("", Cell{}); err == nil {
		t.Fatal("empty definition accepted")
	}
	if _, err := NewFloorRemoval("WoodPlankFloor", Cell{X: -1}); err == nil {
		t.Fatal("negative cell accepted")
	}
	if _, err := NewFloorRemovalAction("", f); err == nil {
		t.Fatal("empty id accepted")
	}
}
