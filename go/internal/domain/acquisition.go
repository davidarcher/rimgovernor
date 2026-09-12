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
