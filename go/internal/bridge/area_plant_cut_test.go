package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An area plant cut builds one AreaPlantCutIntent with canonical cells.
func TestAreaPlantCutBuildsIntent(t *testing.T) {
	value, err := domain.NewAreaPlantCut([]domain.Cell{{X: 5, Z: 2}, {X: 1, Z: 9}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewAreaPlantCutAction("cut1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("area plant cut is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	cells := wire.GetAreaPlantCut().GetCells()
	if len(cells) != 2 || cells[0].GetX() != 1 || cells[0].GetZ() != 9 || cells[1].GetX() != 5 || cells[1].GetZ() != 2 {
		t.Fatalf("%v", wire)
	}
}

func TestAreaPlantCutRefusesBadCells(t *testing.T) {
	for _, cells := range [][]domain.Cell{nil, {{X: -1, Z: 0}}, {{X: 1, Z: 1}, {X: 1, Z: 1}}} {
		if _, err := domain.NewAreaPlantCut(cells); err == nil {
			t.Fatalf("accepted %d cells", len(cells))
		}
	}
	if _, err := domain.NewAreaPlantCutAction("", domain.AreaPlantCut{}); err == nil {
		t.Fatal("accepted an empty action")
	}
}
