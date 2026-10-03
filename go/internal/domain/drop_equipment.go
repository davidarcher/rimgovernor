package domain

import "errors"

// DropEquipment is explicit intent for one pawn to drop the weapon it holds.
// The pawn is not drafted; native eligibility and the weapon being in the
// pawn's hands are established at inspection, not here.
type DropEquipment struct {
	pawn  PawnID
	thing string
}

func NewDropEquipment(pawn PawnID, thing string) (DropEquipment, error) {
	if !validID(string(pawn)) || !validID(thing) || thing == string(pawn) {
		return DropEquipment{}, errors.New("drop equipment requires a valid pawn and a distinct held weapon")
	}
	return DropEquipment{pawn: pawn, thing: thing}, nil
}

func (d DropEquipment) Pawn() PawnID  { return d.pawn }
func (d DropEquipment) Thing() string { return d.thing }

func NewDropEquipmentAction(id ActionID, drop DropEquipment) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewDropEquipment(drop.pawn, drop.thing); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: DropEquipmentAction, dropEquipment: drop}, nil
}

func (a Action) DropEquipment() (DropEquipment, bool) {
	return a.dropEquipment, a.kind == DropEquipmentAction
}
