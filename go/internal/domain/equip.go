package domain

import "errors"

// Equip is explicit intent to arm one already-observed unarmed pawn with one
// already-observed loose weapon. The pawn is not drafted; native reachability,
// current eligibility and job availability are established at inspection, not
// here. Cell is carried the same way Haul/SupplyAllow carry it: a fresh CAS
// token can only be re-queried scoped to a cell, not by thing ID alone.
type Equip struct {
	pawn              PawnID
	thing, definition string
	cell              Cell
}

func NewEquip(pawn PawnID, thing, definition string, cell Cell) (Equip, error) {
	if !validID(string(pawn)) || !validID(thing) || !validID(definition) || cell.X < 0 || cell.Z < 0 {
		return Equip{}, errors.New("equip requires a valid pawn, thing, definition identity and cell")
	}
	return Equip{pawn: pawn, thing: thing, definition: definition, cell: cell}, nil
}

func (e Equip) Pawn() PawnID       { return e.pawn }
func (e Equip) Thing() string      { return e.thing }
func (e Equip) Definition() string { return e.definition }
func (e Equip) Cell() Cell         { return e.cell }

func NewEquipAction(id ActionID, equip Equip) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewEquip(equip.pawn, equip.thing, equip.definition, equip.cell); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: EquipAction, equip: equip}, nil
}

func (a Action) Equip() (Equip, bool) { return a.equip, a.kind == EquipAction }
