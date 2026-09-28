package domain

import "testing"

func TestBedUseIdentity(t *testing.T) {
	if _, err := NewBedMedical("", true); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	b, err := NewBedMedical("bed", true)
	if err != nil {
		t.Fatal(err)
	}
	if b.Thing() != "bed" || !b.Medical() {
		t.Fatal("incorrect bed medical accessors")
	}
}

func TestBedUseAction(t *testing.T) {
	b, err := NewBedMedical("bed", true)
	if err != nil {
		t.Fatal(err)
	}
	action, err := NewBedUseAction("a", b)
	if err != nil {
		t.Fatal(err)
	}
	if action.Kind() != BedUseAction {
		t.Fatal("unexpected kind")
	}
	if got, ok := action.BedUse(); !ok || got != b {
		t.Fatal("bed medical accessor mismatch")
	}
	if _, ok := action.BuildingTemperature(); ok {
		t.Fatal("bed medical action must not read as temperature")
	}
	if _, err := NewBedUseAction("a", BedUse{}); err == nil {
		t.Fatal("zero bed medical accepted")
	}
	if _, err := NewPlan("plan", 1, []Action{action}); err != nil {
		t.Fatal(err)
	}
}

func TestBedSlavesIsItsOwnPatch(t *testing.T) {
	b, err := NewBedSlaves("bed")
	if err != nil || !b.Slaves() || b.Prisoners() || b.Medical() {
		t.Fatal(b, err)
	}
	action, err := NewBedUseAction("a", b)
	if got, ok := action.BedUse(); err != nil || !ok || got != b {
		t.Fatal(got, err)
	}
}
