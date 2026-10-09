package domain

import "errors"

// AutoRefuelAction switches one exact refuelable building's auto-refuel
// (CompRefuelable.allowAutoRefuel): a BuildingPatchIntent arm; no
// pawn or Job is involved. The temperature family lets a heat campfire burn
// out once its sleeping room is warm and refuels it when the room is cold.
const AutoRefuelAction ActionKind = "auto_refuel"

// AutoRefuel is an immutable, comparable value: the building and the
// auto-refuel setting it should hold.
type AutoRefuel struct {
	thing string
	allow bool
}

func NewAutoRefuel(thing string, allow bool) (AutoRefuel, error) {
	if !validID(thing) {
		return AutoRefuel{}, errors.New("invalid auto refuel identity")
	}
	return AutoRefuel{thing, allow}, nil
}
func (b AutoRefuel) Thing() string { return b.thing }
func (b AutoRefuel) Allow() bool   { return b.allow }

func NewAutoRefuelAction(id ActionID, b AutoRefuel) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewAutoRefuel(b.thing, b.allow)
	if err != nil || canonical != b {
		return Action{}, errors.New("invalid auto refuel")
	}
	return Action{id: id, kind: AutoRefuelAction, autoRefuel: b}, nil
}
func (a Action) AutoRefuel() (AutoRefuel, bool) {
	return a.autoRefuel, a.kind == AutoRefuelAction
}
