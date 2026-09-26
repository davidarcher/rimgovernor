package domain

import "errors"

const MoveBuildingAction ActionKind = "move_building"

// MoveBuilding re-sites one exact installed player building (#808) through
// the game's Reinstall: a reinstall blueprint at the destination, then
// ordinary construction work uninstalls the piece and installs it there.
// The piece keeps its identity, quality and hit points; the uninstall waits
// on the piece's reservation, so nobody using it is interrupted. Completion
// is the same building observed installed at the cell and rotation.
type MoveBuilding struct {
	thing, definition string
	cell              Cell
	rotation          Rotation
}

func NewMoveBuilding(thing, definition string, cell Cell, rotation Rotation) (MoveBuilding, error) {
	if !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return MoveBuilding{}, errors.New("invalid move identity or destination")
	}
	switch rotation {
	case North, East, South, West:
	default:
		return MoveBuilding{}, errors.New("invalid move rotation")
	}
	return MoveBuilding{thing, definition, cell, rotation}, nil
}

// Thing is the installed building's load id; it survives the move.
func (m MoveBuilding) Thing() string      { return m.thing }
func (m MoveBuilding) Definition() string { return m.definition }

// Cell and Rotation are the destination placement.
func (m MoveBuilding) Cell() Cell         { return m.cell }
func (m MoveBuilding) Rotation() Rotation { return m.rotation }

func NewMoveBuildingAction(id ActionID, move MoveBuilding) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewMoveBuilding(move.thing, move.definition, move.cell, move.rotation); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MoveBuildingAction, moveBuilding: move}, nil
}
func (a Action) MoveBuilding() (MoveBuilding, bool) {
	return a.moveBuilding, a.kind == MoveBuildingAction
}
