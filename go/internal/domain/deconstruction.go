package domain

import "errors"

const DeconstructionAction ActionKind = "deconstruction"

// Deconstruction designates one observed non-player building. Native safety
// and identity are rechecked at dispatch; only observed demolition completes it.
type Deconstruction struct {
	target, definition string
	cell               Cell
}

func NewDeconstruction(target, definition string, cell Cell) (Deconstruction, error) {
	if !validID(target) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Deconstruction{}, errors.New("invalid deconstruction identity or cell")
	}
	return Deconstruction{target, definition, cell}, nil
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
