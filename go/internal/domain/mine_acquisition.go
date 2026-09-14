package domain

import "errors"

// MineAcquisitionAction is a second, independently-registered acquisition
// vertical alongside AcquisitionAction: intent to dispatch AcquireResource
// against one already-selected native mine source (policy.ResourceSource
// with Method==ResourceSourceMine, populated by
// bridge.ReadResourceSources/policy.SelectResourceSources), keyed by its own
// ActionKind so it can be admitted, dispatched and reconciled through its own
// store admission table and executor/buildingruntime boundary rather than
// sharing AcquisitionAction's single global registration. AcquisitionAction's
// own InspectAcquisition re-validates a target by re-reading the
// AcquisitionFacts census (bridge.ReadAcquisition), which is structurally
// scoped to tree/food/hunt sources only (see contracts/proto/
// observations.proto) — a mined resource can never appear there, so it needs
// this separate vertical's own read (bridge.ReadMineAcquisition, backed by
// ReadResourceSources) instead. The payload shape is identical to
// AcquisitionAction's (one native-approved source thing, its harvested
// resource definition, and its cell), so it reuses the Acquisition value
// type verbatim -- only the ActionKind, admission bookkeeping and native read
// differ. See docs/BACKLOG.md 05.5.
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
