package domain

import "errors"

const DeconstructionAction ActionKind = "deconstruction"

// Deconstruction designates one observed building (a DECONSTRUCT Designate).
// Native safety and identity are checked at apply; applied means designated.
type Deconstruction struct {
	target, definition string
	cell               Cell
	wall               bool // swap the door for a wall
}

// WithWallReplacement returns the deconstruction swapping its door for a
// wall of the door's stuff: native places the wall blueprint and
// orders a builder through both jobs.
func (c Deconstruction) WithWallReplacement() Deconstruction { c.wall = true; return c }

// ReplacesWithWall reports a door-to-wall swap.
func (c Deconstruction) ReplacesWithWall() bool { return c.wall }

// GroundRect is one rectangle of cleared ground: Width x Height
// cells from Origin.
type GroundRect struct {
	Origin        Cell
	Width, Height int32
}

func NewDeconstruction(target, definition string, cell Cell) (Deconstruction, error) {
	if !validID(target) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Deconstruction{}, errors.New("invalid deconstruction identity or cell")
	}
	return Deconstruction{target: target, definition: definition, cell: cell}, nil
}

func (c Deconstruction) Target() string     { return c.target }
func (c Deconstruction) Definition() string { return c.definition }
func (c Deconstruction) Cell() Cell         { return c.cell }
func NewDeconstructionAction(id ActionID, cut Deconstruction) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewDeconstruction(cut.target, cut.definition, cut.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: DeconstructionAction, deconstruction: cut}, nil
}
func (a Action) Deconstruction() (Deconstruction, bool) {
	return a.deconstruction, a.kind == DeconstructionAction
}
