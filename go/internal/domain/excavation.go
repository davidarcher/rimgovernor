package domain

import "errors"

// ExcavationAction designates one rock cell for ordinary pawn mining as one
// step of a staged room/corridor excavation. Unlike MineAcquisitionAction the
// target is a cell and its expected rock definition, never a Mineable thing
// identity (native compressed rocks are recreated with fresh IDs on load),
// and completion is the cell being cleared, not any output yield. Roof
// support, worker access and mining legality are native reads rechecked at
// dispatch and before each pick hit, not properties of this intent.
const ExcavationAction ActionKind = "excavation"

type Excavation struct {
	cell       Cell
	definition string
}

func NewExcavation(cell Cell, definition string) (Excavation, error) {
	if !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Excavation{}, errors.New("invalid excavation definition or cell")
	}
	return Excavation{cell: cell, definition: definition}, nil
}

func (e Excavation) Cell() Cell         { return e.cell }
func (e Excavation) Definition() string { return e.definition }

func NewExcavationAction(id ActionID, excavation Excavation) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewExcavation(excavation.cell, excavation.definition); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: ExcavationAction, excavation: excavation}, nil
}

func (a Action) Excavation() (Excavation, bool) { return a.excavation, a.kind == ExcavationAction }
