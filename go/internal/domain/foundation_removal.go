package domain

import "errors"

const FoundationRemovalAction ActionKind = "foundation_removal"

// FoundationRemoval designates the foundation laid on one cell (a Bridge)
// for removal (#954): a heavy bridge cannot be laid over a plain one. The
// designation is the whole write; ordinary construction work lifts it, and
// a cell whose foundation is already gone or designated applies again.
type FoundationRemoval struct {
	definition string
	cell       Cell
}

func NewFoundationRemoval(definition string, cell Cell) (FoundationRemoval, error) {
	if !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return FoundationRemoval{}, errors.New("invalid foundation removal definition or cell")
	}
	return FoundationRemoval{definition, cell}, nil
}
func (f FoundationRemoval) Definition() string { return f.definition }
func (f FoundationRemoval) Cell() Cell         { return f.cell }

func NewFoundationRemovalAction(id ActionID, f FoundationRemoval) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewFoundationRemoval(f.definition, f.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: FoundationRemovalAction, foundationRemoval: f}, nil
}
func (a Action) FoundationRemoval() (FoundationRemoval, bool) {
	return a.foundationRemoval, a.kind == FoundationRemovalAction
}
