package postmortem

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestExtentEligibilityEvidence(t *testing.T) {
	dir := t.TempDir()
	if err := os.Mkdir(filepath.Join(dir, "service"), 0755); err != nil {
		t.Fatal(err)
	}
	data := `{"path":"/api/routines","status":200,"response":{"resourceReach":{"stage":"base","reason":"threat_present"},"extentEligibility":{"known":true,"reason":"","regions":[{"region":0,"stage":"established","origins":["facility"],"facilities":["bed"],"activeFacilities":[],"eligible":false,"holdReasons":["threat_present","facility_lost","route_unknown"]}]}}}`
	if err := os.WriteFile(filepath.Join(dir, "service", "http-0001.json"), []byte(data), 0644); err != nil {
		t.Fatal(err)
	}
	d := Collect(context.Background(), dir, map[string]any{})
	for _, want := range []string{"stage=established", "origins=[facility]", "active=[]", "threat_present facility_lost route_unknown", "service/http-0001.json"} {
		if !strings.Contains(d.Text(), want) {
			t.Fatalf("missing %s: %s", want, d.Text())
		}
	}
}
