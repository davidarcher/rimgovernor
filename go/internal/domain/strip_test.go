package domain

import "testing"

func TestStripActionRequiresTarget(t *testing.T) {
	if _, err := NewStrip(""); err == nil {
		t.Fatal("empty target accepted")
	}
	strip, err := NewStrip("Pawn_Raider1")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewStripAction("strip-0", strip)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := a.Strip(); !ok || got.Target() != "Pawn_Raider1" || a.Kind() != StripAction {
		t.Fatalf("strip action = %+v %v", got, ok)
	}
	if _, ok := a.CutPlant(); ok {
		t.Fatal("strip reads as cut plant")
	}
}
