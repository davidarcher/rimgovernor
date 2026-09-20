package medical

import "testing"

func TestDiseaseRequiresNativeImmunityAndLivingPatient(t *testing.T) {
	patient := &diseasePatient{}
	patients := map[string]*diseasePatient{"patient": patient}
	condition := map[string]any{"definition": map[string]any{"defName": "Plague"}, "immunity": .9, "tended": true}
	row := map[string]any{"pawn": map[string]any{"id": "patient"}, "dead": false, "inBed": true,
		"settings": map[string]any{"medicalCare": "NormalOrWorse"}, "health": map[string]any{"hediffs": []any{condition}}}
	observed := map[string]any{"pawns": []any{row}}
	if err := diseaseObserve(observed, patients); err != nil {
		t.Fatal(err)
	}
	if patient.Immune || !patient.Tier || !patient.Tended || !patient.Rested {
		t.Fatalf("unexpected evidence: %+v", patient)
	}
	if patient.BedRestEnabled {
		t.Fatal("absent work settings certified bed rest")
	}
	row["settings"] = map[string]any{"work": []any{map[string]any{"defName": "PatientBedRest", "priority": float64(1)}}}
	if err := diseaseObserve(observed, patients); err != nil || !patient.BedRestEnabled {
		t.Fatalf("enabled bed rest not observed: %v", err)
	}
	delete(row, "health")
	if err := diseaseObserve(observed, patients); err != nil || patient.Immune {
		t.Fatalf("partial health certified immunity: %v %+v", err, patient)
	}
	condition["immunity"] = float64(1)
	row["health"] = map[string]any{"hediffs": []any{condition}}
	if err := diseaseObserve(observed, patients); err != nil || !patient.Immune {
		t.Fatalf("full immunity not observed: %v", err)
	}
	row["dead"] = true
	if err := diseaseObserve(observed, patients); err == nil {
		t.Fatal("immune patient's death accepted")
	}
}
