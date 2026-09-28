package domain

import "errors"

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
// one source (#1046): the planner's stall withdraw, filed as its own
// one-action method. Like UninstallBuildingAction it shares its intent arm
// (AcquireIntent, withdraw=true) with the acquisition that placed it.
const AcquisitionWithdrawAction ActionKind = "acquisition_withdraw"

func NewAcquisitionWithdrawAction(id ActionID, acquisition Acquisition) (Action, error) {
	a, err := NewAcquisitionAction(id, acquisition)
	a.kind = AcquisitionWithdrawAction
	return a, err
}
func (a Action) AcquisitionWithdraw() (Acquisition, bool) {
	return a.acquisition, a.kind == AcquisitionWithdrawAction
}

// AcquireIntent is the AcquireIntent payload of an acquisition, a mine
// acquisition or a withdraw.
func (a Action) AcquireIntent() (acquisition Acquisition, withdraw, ok bool) {
	switch a.kind {
	case AcquisitionAction, AcquisitionWithdrawAction:
		return a.acquisition, a.kind == AcquisitionWithdrawAction, true
	case MineAcquisitionAction:
		return a.mineAcquisition, false, true
	}
	return Acquisition{}, false, false
}
