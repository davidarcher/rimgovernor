package domain

import "errors"

const CutPlantAction ActionKind = "cut_plant"

// CutPlant designates one observed blighted plant for cutting (#245): the
// exact plant by identity, never everything at a map cell. The designation
// is the whole write; ordinary plant-cutting work does the cut.
type CutPlant struct {
	plant, definition string
	cell              Cell
}

func NewCutPlant(plant, definition string, cell Cell) (CutPlant, error) {
	if !validID(plant) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return CutPlant{}, errors.New("invalid plant identity or cell")
	}
	return CutPlant{plant, definition, cell}, nil
}
func (c CutPlant) Plant() string      { return c.plant }
func (c CutPlant) Definition() string { return c.definition }
func (c CutPlant) Cell() Cell         { return c.cell }
func NewCutPlantAction(id ActionID, cut CutPlant) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewCutPlant(cut.plant, cut.definition, cut.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: CutPlantAction, cutPlant: cut}, nil
}
func (a Action) CutPlant() (CutPlant, bool) { return a.cutPlant, a.kind == CutPlantAction }
