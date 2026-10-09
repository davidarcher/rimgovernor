package domain

import "errors"

// WastepackHaulAction designates one exact unprotected wastepack for hauling
// to storage: DesignateIntent HAUL on the pack under the wastepack
// guard, which native holds to a pack that is neither frozen nor inside an
// atomizer. The designation is the whole write; a hauler carries the pack to
// a stockpile that accepts it, and the goal settles on the pack being frozen
// or atomized, never on the order.
const WastepackHaulAction ActionKind = "wastepack_haul"

// WastepackHaul names the wastepack stack by identity, definition and cell.
type WastepackHaul struct {
	thing, definition string
	cell              Cell
}

func NewWastepackHaul(thing, definition string, cell Cell) (WastepackHaul, error) {
	if !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return WastepackHaul{}, errors.New("invalid wastepack haul identity or cell")
	}
	return WastepackHaul{thing, definition, cell}, nil
}
func (w WastepackHaul) Thing() string      { return w.thing }
func (w WastepackHaul) Definition() string { return w.definition }
func (w WastepackHaul) Cell() Cell         { return w.cell }

func NewWastepackHaulAction(id ActionID, haul WastepackHaul) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewWastepackHaul(haul.thing, haul.definition, haul.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: WastepackHaulAction, wastepackHaul: haul}, nil
}
func (a Action) WastepackHaul() (WastepackHaul, bool) {
	return a.wastepackHaul, a.kind == WastepackHaulAction
}
