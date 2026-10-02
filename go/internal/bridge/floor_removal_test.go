package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A floor removal builds one RemoveFloorIntent (epic #1249) on its cell.
func TestFloorRemovalBuildsIntent(t *testing.T) {
	value, err := domain.NewFloorRemoval("WoodPlankFloor", domain.Cell{X: 3, Z: 9})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewFloorRemovalAction("f1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("floor removal is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	if r := wire.GetRemoveFloor(); r.GetDefName() != "WoodPlankFloor" || r.GetCell().GetX() != 3 || r.GetCell().GetZ() != 9 {
		t.Fatalf("%v", wire)
	}
}
