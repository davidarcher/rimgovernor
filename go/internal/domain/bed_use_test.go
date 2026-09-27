package domain

import "testing"

func TestBedUseIdentity(t *testing.T) {
	if _, err := NewBedMedical("", true, "token"); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	if _, err := NewBedMedical("bed", true, ""); err == nil {
		t.Fatal("expected empty before token to be rejected")
	}
	b, err := NewBedMedical("bed", true, "token")
	if err != nil {
		t.Fatal(err)
	}
	if b.Thing() != "bed" || !b.Medical() || b.BeforeToken() != "token" {
		t.Fatal("incorrect bed medical accessors")
	}
}

func TestBedUseAction(t *testing.T) {
	b, err := NewBedMedical("bed", true, "token")
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
