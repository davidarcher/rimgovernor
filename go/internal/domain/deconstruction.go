package domain

import "errors"

const DeconstructionAction ActionKind = "deconstruction"

// Deconstruction designates one observed non-player building. Native safety
// and identity are rechecked at dispatch; only observed demolition completes it.
// A breach (#458) is the same designation on a sealed ancient shrine's
// perimeter wall: the dispatch guard reads the shrine census instead of the
// Home clearance census, since the wall is an ancient danger by definition.
type Deconstruction struct {
	target, definition string
	cell               Cell
	breach             bool
}

func NewDeconstruction(target, definition string, cell Cell) (Deconstruction, error) {
	if !validID(target) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Deconstruction{}, errors.New("invalid deconstruction identity or cell")
	}
	return Deconstruction{target: target, definition: definition, cell: cell}, nil
}

// NewBreachDeconstruction is a Deconstruction of a sealed shrine's breach wall.
func NewBreachDeconstruction(target, definition string, cell Cell) (Deconstruction, error) {
	out, err := NewDeconstruction(target, definition, cell)
	out.breach = err == nil
	return out, err
}
func (c Deconstruction) Target() string     { return c.target }
func (c Deconstruction) Breach() bool       { return c.breach }
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
