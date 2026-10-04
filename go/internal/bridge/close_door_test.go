package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// A close_door (#1743) is the existing combat_orders door CLOSE order for its
// cell: no pawn, one door order.
func TestCloseDoorBuildsCombatDoorClose(t *testing.T) {
	value, err := domain.NewCloseDoor(domain.Cell{X: 3, Z: 9})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewCloseDoorAction("d1", value)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("close_door is not an intent kind")
	}
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	orders := wire.GetCombatOrders().GetOrders()
	if len(orders) != 1 || orders[0].GetPawn() != nil {
		t.Fatalf("%v", wire)
	}
	if d := orders[0].GetDoor(); d.GetCell().GetX() != 3 || d.GetCell().GetZ() != 9 || d.GetMode() != o.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE {
		t.Fatalf("%v", wire)
	}
}

func TestCloseDoorRejectsInvalid(t *testing.T) {
	if _, err := domain.NewCloseDoor(domain.Cell{X: -1, Z: 1}); err == nil {
		t.Fatal("negative cell accepted")
	}
}
