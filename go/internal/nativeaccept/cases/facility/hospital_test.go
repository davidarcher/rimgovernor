package facility

import "testing"

// Only a completed plan made entirely of bed_medical patches ends the
// watch; the ladder's shell and bed plans share the prefix and never do.
func TestBedConvertedSeesOnlyCompletedMedicalPatches(t *testing.T) {
	t.Parallel()
	convert := map[string]any{"plan": "routine-hospital-1", "actions": 1, "stages": map[string]int{"completed": 1}, "kinds": map[string]int{"bed_medical": 1}}
	open := map[string]any{"plan": "routine-hospital-1", "actions": 1, "stages": map[string]int{"awaiting_observation": 1}, "kinds": map[string]int{"bed_medical": 1}}
	spot := map[string]any{"plan": "routine-hospital-2", "actions": 1, "stages": map[string]int{"completed": 1}, "kinds": map[string]int{"building": 1}}
	shell := map[string]any{"plan": "routine-hospital-3", "actions": 32, "stages": map[string]int{"completed": 32}, "kinds": map[string]int{"building": 32}}
	if bedConverted(map[string]any{"plans": []map[string]any{open, spot}, "retired_plans": []map[string]any{shell}}) {
		t.Fatal("a ladder plan or an open patch counted as converted")
	}
	if !bedConverted(map[string]any{"retired_plans": []map[string]any{shell, convert}}) {
		t.Fatal("a completed retired patch did not count")
	}
	if bedConverted(map[string]any{}) {
		t.Fatal("empty sample counted")
	}
}
