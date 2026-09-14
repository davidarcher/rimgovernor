package domain

import "errors"

// Movement is explicit intent to walk one already-drafted pawn to one exact
// cell, layered on an existing owned draft exactly like MeleeAttack and
// RangedAttack. It proves nothing about pathability, reachability or
// permission to dispatch a job.
type Movement struct {
	pawn        PawnID
	destination Cell
	draftAction ActionID
}

func NewMovement(pawn PawnID, destination Cell, prerequisite ActionID) (Movement, error) {
	if !validID(string(pawn)) {
		return Movement{}, errors.New("movement requires a valid pawn identity")
	}
	if destination.X < 0 || destination.Z < 0 {
		return Movement{}, errors.New("movement destination must be nonnegative")
	}
	if !validID(string(prerequisite)) {
		return Movement{}, errors.New("invalid movement draft prerequisite")
	}
	return Movement{pawn: pawn, destination: destination, draftAction: prerequisite}, nil
}

func (m Movement) Pawn() PawnID          { return m.pawn }
func (m Movement) Destination() Cell     { return m.destination }
func (m Movement) DraftAction() ActionID { return m.draftAction }

func NewMovementAction(id ActionID, movement Movement) (Action, error) {
	if !validID(string(id)) || id == movement.draftAction {
		return Action{}, errors.New("invalid movement action identity or self prerequisite")
	}
	if _, err := NewMovement(movement.pawn, movement.destination, movement.draftAction); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MovementAction, movement: movement}, nil
}

func (a Action) Movement() (Movement, bool) { return a.movement, a.kind == MovementAction }
