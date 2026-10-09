package domain

import "errors"

const MoveBuildingAction ActionKind = "move_building"

// MoveBuilding re-sites one exact installed player building through
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

const UninstallBuildingAction ActionKind = "uninstall_building"

// UninstallBuilding packs one exact installed player building
// through the game's Uninstall designation: ordinary work minifies it where
// it stands and vanilla hauling takes the packed item to storage. Its value
// is the building's current placement (MoveBuilding's shape), which the
// native effect echoes; the uninstall waits on the building's reservation
// like a move. Completion is the building observed packed.
type UninstallBuilding = MoveBuilding

func NewUninstallBuildingAction(id ActionID, building UninstallBuilding) (Action, error) {
	a, err := NewMoveBuildingAction(id, building)
	if err != nil {
		return Action{}, err
	}
	a.kind = UninstallBuildingAction
	return a, nil
}
func (a Action) UninstallBuilding() (UninstallBuilding, bool) {
	return a.moveBuilding, a.kind == UninstallBuildingAction
}

// Relocation is the building of a move or an uninstall: the two kinds share
// the executor, admission and bridge plumbing and differ only in the
// operation and the stages it reports.
func (a Action) Relocation() (building MoveBuilding, uninstall, ok bool) {
	return a.moveBuilding, a.kind == UninstallBuildingAction, a.kind == MoveBuildingAction || a.kind == UninstallBuildingAction
}
