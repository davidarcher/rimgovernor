package domain

import "testing"

func TestSurgeryRequiresPatientRecipeAndPart(t *testing.T) {
	if _, err := NewSurgery("", "InstallPegLeg", 3, false); err == nil {
		t.Fatal("empty patient accepted")
	}
	if _, err := NewSurgery("Human1", "", 3, false); err == nil {
		t.Fatal("empty recipe accepted")
	}
	if _, err := NewSurgery("Human1", "InstallPegLeg", -2, false); err == nil {
		t.Fatal("negative part accepted")
	}
	v, err := NewSurgery("Human1", "HarvestOrgan", NoSurgeryPart, true)
	if err != nil || !v.AcknowledgeViolation() || v.Part() != NoSurgeryPart {
		t.Fatal(v, err)
	}
	a, err := NewSurgeryAction("s1", v)
	if got, ok := a.Surgery(); err != nil || !ok || got != v || a.Kind() != SurgeryAction {
		t.Fatal(a, err)
	}
	if _, err := NewSurgeryAction("s1", Surgery{}); err == nil {
		t.Fatal("zero surgery accepted")
	}
}
