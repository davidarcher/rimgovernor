package domain

import "errors"

// CloseDoorAction is explicit intent to clear the hold-open of one player
// door (#1743, epic #1694): the CombatOrders door CLOSE order, which clears
// Building_Door.holdOpenInt so the door shuts once nothing stands in it.
// Applied means the order was taken; that the door then closed is a separate
// observed state. Native refuses a cell that is not a player door.
const CloseDoorAction ActionKind = "close_door"

// CloseDoor is one door cell to close.
type CloseDoor struct{ cell Cell }

func NewCloseDoor(cell Cell) (CloseDoor, error) {
	if cell.X < 0 || cell.Z < 0 {
		return CloseDoor{}, errors.New("close door requires a valid cell")
	}
	return CloseDoor{cell: cell}, nil
}

func (d CloseDoor) Cell() Cell { return d.cell }

func NewCloseDoorAction(id ActionID, door CloseDoor) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewCloseDoor(door.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: CloseDoorAction, closeDoor: door}, nil
}

func (a Action) CloseDoor() (CloseDoor, bool) { return a.closeDoor, a.kind == CloseDoorAction }
