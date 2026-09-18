package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The issue #6 acceptance matrix must keep one row per criterion in the
// issue text, each naming a harness that exists, so a typo or a dropped row
// fails go test rather than a spent native session.
func TestIssue6MatrixCoversEveryCriterion(t *testing.T) {
	path := filepath.Join("suites", "issue-6-matrix.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	var list []harness
	if err := decoder.Decode(&list); err != nil {
		t.Fatal(err)
	}
	if _, err := loadSuite("", path); err != nil {
		t.Fatal(err)
	}
	criteria := map[string]bool{
		"dark benches": false, "partially lit benches": false, "protected fungus rooms": false,
		"filthy vs inherently dirty rooms": false, "kitchen/butcher separation": false, "unreachable stores": false,
		"disconnected consumers": false, "exhausted fuel": false, "exhausted batteries": false, "hot-weather freezer failure": false,
	}
	for _, h := range list {
		if h.Binary == "" || filepath.IsAbs(h.Binary) || !strings.HasSuffix(h.Binary, ".exe") {
			t.Errorf("%s: binary %q must be a harness name relative to -bin", h.Name, h.Binary)
		}
		if _, err := os.Stat(filepath.Join("..", strings.TrimSuffix(h.Binary, ".exe"), "main.go")); err != nil {
			t.Errorf("%s: no harness command for %s: %v", h.Name, h.Binary, err)
		}
		if len(h.Args) < 2 || h.Args[0] != "-rimgovernor" || h.Args[1] != "{rimgovernor}" {
			t.Errorf("%s: args must start with -rimgovernor {rimgovernor}, got %v", h.Name, h.Args)
		}
		if h.Acceptance == "" {
			t.Errorf("%s: acceptance criterion missing", h.Name)
		}
		for criterion := range criteria {
			if strings.HasPrefix(h.Acceptance, criterion+":") {
				criteria[criterion] = true
			}
		}
	}
	for criterion, covered := range criteria {
		if !covered {
			t.Errorf("criterion %q has no row", criterion)
		}
	}
	if got := resolveBinary(filepath.Join("x", "bin"), list[0]); got != filepath.Join("x", "bin", list[0].Binary) {
		t.Errorf("relative binary resolved to %q", got)
	}
}
