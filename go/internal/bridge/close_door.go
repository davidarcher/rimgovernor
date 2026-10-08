package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// doorControlAction is the Actions/Apply arm of one door_control action (#1743):
// the existing combat_orders door CLOSE order for the cell, so no new native
// intent. Native refuses a cell that is no player door as a result in the
// CombatOrdersEffect (refusal not_a_door); the door facts the cell came from
// are read from that same door, so the next review sees a door still held
// open when an order did not take.
func doorControlAction(action domain.Action) (*o.Action, error) {
	door, ok := action.DoorControl()
	if !ok {
		return nil, contract("not a door_control action")
	}
	if _, err := domain.NewDoorControl(door.Cell(), door.HoldOpen()); err != nil {
		return nil, contract("door_control: %v", err)
	}
	mode := o.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE
	if door.HoldOpen() {
		mode = o.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN
	}
	command := &o.CombatOrders{Orders: []*o.CombatOrder{{Order: &o.CombatOrder_Door{Door: &o.CombatDoor{
		Cell: &c.Cell{X: proto.Int32(door.Cell().X), Z: proto.Int32(door.Cell().Z)},
		Mode: mode.Enum()}}}}}
	if err := ValidateCombatOrders(command); err != nil {
		return nil, err
	}
	return &o.Action{Intent: &o.Action_CombatOrders{CombatOrders: command}}, nil
}
