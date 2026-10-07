package affected

import (
	"slices"
	"testing"
)

func TestControllerEntryPointSelectsServeHostingAreas(t *testing.T) {
	const file = "go/cmd/rimgovernor/serve_building.go"
	sel, err := Select(repo(t), []string{file})
	if err != nil {
		t.Fatal(err)
	}
	for _, area := range []string{"light", "upkeep", "lifecycle"} {
		if !slices.Contains(sel.Cases, area) {
			t.Errorf("entry-point change omitted serve-hosting area %s: %v", area, sel.Cases)
		}
		wantWhy := []string{"the rimgovernor binary changed (" + file + ")"}
		if !slices.Equal(sel.Why[area], wantWhy) {
			t.Errorf("%s reasons = %v, want %v", area, sel.Why[area], wantWhy)
		}
	}
	for _, area := range []string{"authority", "bed", "zone"} {
		if slices.Contains(sel.Cases, area) {
			t.Errorf("entry-point change selected bridge-only area %s", area)
		}
	}
	if sel.AllHarnesses || len(sel.Sampled) != 0 {
		t.Errorf("entry-point change must select full serve-hosting areas only: %+v", sel)
	}
}
