package domain

import "errors"

// MineAcquisitionAction designates one already-selected native mine source
// (policy.ResourceSource with Method==ResourceSourceMine) through the same
// Actions/Apply acquisition Designate as AcquisitionAction (#1046). It keeps its own
// kind because the resource planner, not the census acquisition planner,
// owns it; the payload is the same Acquisition value.
const MineAcquisitionAction ActionKind = "mine_acquisition"

func NewMineAcquisitionAction(id ActionID, acquisition Acquisition) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewAcquisition(acquisition.thing, acquisition.definition, acquisition.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MineAcquisitionAction, mineAcquisition: acquisition}, nil
}

func (a Action) MineAcquisition() (Acquisition, bool) {
	return a.mineAcquisition, a.kind == MineAcquisitionAction
}
