package domain

import "errors"

const FloorRemovalAction ActionKind = "floor_removal"

// FloorRemoval designates the constructed floor laid on one cell for removal
// (vanilla RemoveFloor) so clearance can free planned ground. The
// designation is the whole write; ordinary construction work removes the
// floor, and a cell whose floor is already gone or designated applies again.
type FloorRemoval struct {
	definition string
	cell       Cell
}

func NewFloorRemoval(definition string, cell Cell) (FloorRemoval, error) {
	if !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return FloorRemoval{}, errors.New("invalid floor removal definition or cell")
	}
	return FloorRemoval{definition, cell}, nil
}
func (f FloorRemoval) Definition() string { return f.definition }
func (f FloorRemoval) Cell() Cell         { return f.cell }

func NewFloorRemovalAction(id ActionID, f FloorRemoval) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewFloorRemoval(f.definition, f.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: FloorRemovalAction, floorRemoval: f}, nil
}
func (a Action) FloorRemoval() (FloorRemoval, bool) {
	return a.floorRemoval, a.kind == FloorRemovalAction
}
