package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A build roof builds one BuildRoofIntent with canonical cells.
func TestBuildRoofBuildsIntent(t *testing.T) {
	value, err := domain.NewBuildRoof([]domain.Cell{{X: 5, Z: 2}, {X: 1, Z: 9}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildRoofAction("r1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("build roof is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	cells := wire.GetBuildRoof().GetCells()
	if len(cells) != 2 || cells[0].GetX() != 1 || cells[0].GetZ() != 9 || cells[1].GetX() != 5 || cells[1].GetZ() != 2 {
		t.Fatalf("%v", wire)
	}
}

func TestBuildRoofRefusesBadCells(t *testing.T) {
	for _, cells := range [][]domain.Cell{nil, {{X: -1, Z: 0}}, {{X: 1, Z: 1}, {X: 1, Z: 1}}} {
		if _, err := domain.NewBuildRoof(cells); err == nil {
			t.Fatalf("accepted %v", cells)
		}
	}
}
