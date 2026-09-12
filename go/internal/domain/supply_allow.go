package domain

import "errors"

const SupplyAllowAction ActionKind = "supply_allow"

// SupplyAllow names one observed loose item, never everything at a map cell.
type SupplyAllow struct {
	thing, definition string
	cell              Cell
}

func NewSupplyAllow(thing, definition string, cell Cell) (SupplyAllow, error) {
	if !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return SupplyAllow{}, errors.New("invalid supply identity or cell")
	}
	return SupplyAllow{thing, definition, cell}, nil
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
	return Action{id: id, kind: SupplyAllowAction, supply: supply}, nil
}
func (a Action) SupplyAllow() (SupplyAllow, bool) { return a.supply, a.kind == SupplyAllowAction }
