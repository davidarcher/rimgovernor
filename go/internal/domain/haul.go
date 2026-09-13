package domain

import "errors"

// Haul is explicit intent to move one already-observed loose thing. The pawn is
// not drafted; native storage search picks the destination, and current
// eligibility and job availability are established at inspection, not here.
// Cell is carried the same way SupplyAllow carries it: a fresh CAS token can
// only be re-queried scoped to a cell, not by thing ID alone.
type Haul struct {
	pawn              PawnID
	thing, definition string
	cell              Cell
}

func NewHaul(pawn PawnID, thing, definition string, cell Cell) (Haul, error) {
	if !validID(string(pawn)) || !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Haul{}, errors.New("haul requires a valid pawn, thing, definition identity and cell")
	}
	return Haul{pawn: pawn, thing: thing, definition: definition, cell: cell}, nil
}

func (h Haul) Pawn() PawnID       { return h.pawn }
func (h Haul) Thing() string      { return h.thing }
func (h Haul) Definition() string { return h.definition }
func (h Haul) Cell() Cell         { return h.cell }

func NewHaulAction(id ActionID, haul Haul) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewHaul(haul.pawn, haul.thing, haul.definition, haul.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: HaulAction, haul: haul}, nil
}

func (a Action) Haul() (Haul, bool) { return a.haul, a.kind == HaulAction }
