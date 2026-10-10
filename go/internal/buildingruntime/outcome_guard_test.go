package buildingruntime

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestNoCatchAllKindOrHandBuiltRefusal keeps the closed set closed: no kind
// is named for "something else", and a refusal or wait is built only by
// refuse and waitOn (which validate), never as a literal in a planner.
func TestNoCatchAllKindOrHandBuiltRefusal(t *testing.T) {
	for _, kind := range append(append([]policy.Cause{}, refusalKinds...), waitKinds...) {
		for _, vague := range []string{"unknown", "other", "generic", "unspecified", "misc", "fallback"} {
			if strings.Contains(string(kind), vague) {
				t.Fatalf("kind %q is a catch-all (%s)", kind, vague)
			}
		}
	}
	files, err := filepath.Glob("*.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, file := range files {
		if file == "outcome.go" || strings.HasSuffix(file, "_test.go") {
			continue
		}
		data, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		for _, literal := range []string{"Refusal{", "Outcome: OutcomeRefused", "Outcome: OutcomeWaiting", "Refusal.Kind ="} {
			if strings.Contains(string(data), literal) {
				t.Errorf("%s builds a verdict by hand (%s); use refuse, waitOn or a shared verdict", file, literal)
			}
		}
	}
}
