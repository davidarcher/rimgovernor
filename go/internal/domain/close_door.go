package domain

import "errors"

// DoorControlAction sets one player door's hold-open latch through CombatOrders.
// Applied confirms the setting; physical opening or closing requires a separate
// observation. Opening requires ordinary pawn passage; closing waits for clearance.
const DoorControlAction ActionKind = "door_control"

// DoorControl is the desired hold-open setting for one door cell.
type DoorControl struct {
	cell     Cell
	holdOpen bool
}

func NewDoorControl(cell Cell, holdOpen bool) (DoorControl, error) {
	if cell.X < 0 || cell.Z < 0 {
		return DoorControl{}, errors.New("door control requires a valid cell")
	}
	return DoorControl{cell: cell, holdOpen: holdOpen}, nil
}

func (d DoorControl) Cell() Cell     { return d.cell }
func (d DoorControl) HoldOpen() bool { return d.holdOpen }

func NewDoorControlAction(id ActionID, door DoorControl) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewDoorControl(door.cell, door.holdOpen); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: DoorControlAction, doorControl: door}, nil
}

func (a Action) DoorControl() (DoorControl, bool) { return a.doorControl, a.kind == DoorControlAction }
