package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A remove roof builds one RemoveRoofIntent with canonical cells.
func TestRemoveRoofBuildsIntent(t *testing.T) {
	value, err := domain.NewRemoveRoof([]domain.Cell{{X: 5, Z: 2}, {X: 1, Z: 9}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRemoveRoofAction("r1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("remove roof is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	cells := wire.GetRemoveRoof().GetCells()
	if len(cells) != 2 || cells[0].GetX() != 1 || cells[0].GetZ() != 9 || cells[1].GetX() != 5 || cells[1].GetZ() != 2 {
		t.Fatalf("%v", wire)
	}
}

func TestRemoveRoofRefusesBadCells(t *testing.T) {
	for _, cells := range [][]domain.Cell{nil, {{X: -1, Z: 0}}, {{X: 1, Z: 1}, {X: 1, Z: 1}}} {
		if _, err := domain.NewRemoveRoof(cells); err == nil {
			t.Fatalf("accepted %v", cells)
		}
	}
}
