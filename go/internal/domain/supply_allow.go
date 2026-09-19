package domain

import "errors"

const SupplyAllowAction ActionKind = "supply_allow"
const SupplyForbidAction ActionKind = "supply_forbid"

// SupplyAllow names one observed loose item, never everything at a map cell.
type SupplyAllow struct {
	thing, definition string
	cell              Cell
	forbid            bool
}

func NewSupplyAllow(thing, definition string, cell Cell) (SupplyAllow, error) {
	if !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return SupplyAllow{}, errors.New("invalid supply identity or cell")
	}
	return SupplyAllow{thing: thing, definition: definition, cell: cell}, nil
}
func NewSupplyForbid(thing, definition string, cell Cell) (SupplyAllow, error) {
	s, err := NewSupplyAllow(thing, definition, cell)
	s.forbid = true
	return s, err
}
func (s SupplyAllow) Forbidden() bool { return s.forbid }
func (s SupplyAllow) Designation() string {
	if s.forbid {
		return "Forbid"
	}
	return "Allow"
}
func (s SupplyAllow) Thing() string      { return s.thing }
func (s SupplyAllow) Definition() string { return s.definition }
func (s SupplyAllow) Cell() Cell         { return s.cell }
func NewSupplyAllowAction(id ActionID, supply SupplyAllow) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewSupplyAllow(supply.thing, supply.definition, supply.cell); err != nil {
		return Action{}, err
	}
	kind := SupplyAllowAction
	if supply.forbid {
		kind = SupplyForbidAction
	}
	return Action{id: id, kind: kind, supply: supply}, nil
}
func (a Action) SupplyAllow() (SupplyAllow, bool) {
	return a.supply, a.kind == SupplyAllowAction || a.kind == SupplyForbidAction
}
