package domain

import "testing"

func TestMedicalCareAssignmentCeiling(t *testing.T) {
	for _, care := range []string{"NoMeds", "HerbalOrWorse", "NormalOrWorse"} {
		w, err := NewMedicalCareAssignment("p", "before", care)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := NewWorkAssignmentAction("a", w); err != nil {
			t.Fatal(err)
		}
		w.hasArea = true
		if _, err := NewWorkAssignmentAction("a", w); err == nil {
			t.Fatal("mixed care/area accepted")
		}
	}
	for _, care := range []string{"", "Best", "NoCare", "unknown"} {
		if _, err := NewMedicalCareAssignment("p", "before", care); err == nil {
			t.Fatal(care)
		}
	}
}
