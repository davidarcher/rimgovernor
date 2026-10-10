package stateval

import (
	"errors"
	"fmt"
	"os"
	"slices"
	"strings"
	"testing"
)

func TestNotMirroredNamesClassAndFact(t *testing.T) {
	err := fmt.Errorf("evaluate MarketValue: %w", &NotMirrored{Class: "StatPart_Quality", Fact: "quality factor"})
	var nm *NotMirrored
	if !errors.As(err, &nm) || nm.Class != "StatPart_Quality" || nm.Fact != "quality factor" {
		t.Fatalf("errors.As = %v, %+v", errors.As(err, &nm), nm)
	}
	want := "stat class StatPart_Quality: quality factor is computed in game code and not mirrored"
	if nm.Error() != want {
		t.Errorf("Error() = %q, want %q", nm.Error(), want)
	}
}

// TestOwnerColumnMatchesEvaluator: stat_classes.tsv names an owner for exactly
// the classes the evaluator ports, as stateval.<Class>.
func TestOwnerColumnMatchesEvaluator(t *testing.T) {
	raw, err := os.ReadFile("../../../cmd/stataudit/stat_classes.tsv")
	if err != nil {
		t.Fatal(err)
	}
	var owned []string
	for _, line := range strings.Split(strings.TrimSpace(string(raw)), "\n")[1:] {
		f := strings.Split(line, "\t")
		class, kind, owner := f[0], f[1], f[len(f)-1]
		if owner == "unowned" {
			continue
		}
		if owner != "stateval."+class {
			t.Errorf("%s (%s) is owned by %q: a ported class is owned as stateval.<Class>", class, kind, owner)
		}
		owned = append(owned, class)
	}
	slices.Sort(owned)
	if got := OwnedClasses(); !slices.Equal(owned, got) {
		t.Errorf("stat_classes.tsv owns %v, the evaluator ports %v", owned, got)
	}
}
