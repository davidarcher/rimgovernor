package domain

import (
	"errors"
	"strings"
)

const AcquisitionAction ActionKind = "acquisition"

// Acquisition names one native-approved source and its harvested resource.
type Acquisition struct {
	thing, definition string
	cell              Cell
}

func NewAcquisition(thing, definition string, cell Cell) (Acquisition, error) {
	if !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Acquisition{}, errors.New("invalid acquisition identity or cell")
	}
	return Acquisition{thing, definition, cell}, nil
}
func (s Acquisition) Thing() string      { return s.thing }
func (s Acquisition) Definition() string { return s.definition }
func (s Acquisition) Cell() Cell         { return s.cell }

// Hunt reports a hunt: the acquired resource of an animal is its corpse.
func (s Acquisition) Hunt() bool { return strings.HasPrefix(s.definition, "Corpse_") }
func NewAcquisitionAction(id ActionID, acquisition Acquisition) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewAcquisition(acquisition.thing, acquisition.definition, acquisition.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: AcquisitionAction, acquisition: acquisition}, nil
}
func (a Action) Acquisition() (Acquisition, bool) { return a.acquisition, a.kind == AcquisitionAction }

// AcquisitionWithdrawAction removes the harvest, hunt or mine designation on
// one source: the planner's stall withdraw, filed as its own
// one-action method. Like UninstallBuildingAction it shares its intent arm
// (the acquisition Designate, withdraw=true) with the acquisition that placed it.
const AcquisitionWithdrawAction ActionKind = "acquisition_withdraw"

func NewAcquisitionWithdrawAction(id ActionID, acquisition Acquisition) (Action, error) {
	a, err := NewAcquisitionAction(id, acquisition)
	a.kind = AcquisitionWithdrawAction
	return a, err
}
func (a Action) AcquisitionWithdraw() (Acquisition, bool) {
	return a.acquisition, a.kind == AcquisitionWithdrawAction
}

// AcquireIntent is the acquisition Designate payload of an acquisition, a
// mine acquisition or a withdraw; mine reports a mine acquisition.
func (a Action) AcquireIntent() (acquisition Acquisition, withdraw, ok bool) {
	switch a.kind {
	case AcquisitionAction, AcquisitionWithdrawAction:
		return a.acquisition, a.kind == AcquisitionWithdrawAction, true
	case MineAcquisitionAction:
		return a.mineAcquisition, false, true
	}
	return Acquisition{}, false, false
}
