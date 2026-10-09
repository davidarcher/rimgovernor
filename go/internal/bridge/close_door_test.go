package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// A door_control is the existing combat_orders door CLOSE order for its
// cell: no pawn, one door order.
func TestDoorControlBuildsCombatDoorClose(t *testing.T) {
	for _, held := range []bool{false, true} {
		value, err := domain.NewDoorControl(domain.Cell{X: 3, Z: 9}, held)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewDoorControlAction("d1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("door_control is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		orders := wire.GetCombatOrders().GetOrders()
		if len(orders) != 1 || orders[0].GetPawn() != nil {
			t.Fatalf("%v", wire)
		}
		want := o.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE
		if held {
			want = o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN
		}
		if d := orders[0].GetDoor(); d.GetCell().GetX() != 3 || d.GetCell().GetZ() != 9 || d.GetMode() != want {
			t.Fatalf("%v", wire)
		}
	}
}

func TestDoorControlRejectsInvalid(t *testing.T) {
	if _, err := domain.NewDoorControl(domain.Cell{X: -1, Z: 1}, false); err == nil {
		t.Fatal("negative cell accepted")
	}
}
