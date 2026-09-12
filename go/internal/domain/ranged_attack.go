package domain

import "errors"

// RangedAttack is explicit intent, layered on an existing owned draft exactly
// like MeleeAttack. It proves nothing about a ranged weapon, line of sight,
// reachability or permission to dispatch a job; explosive launchers stay
// outside this contract entirely (see policy.EvaluateRangedDefense).
type RangedAttack struct {
	pawn, target PawnID
	draftAction  ActionID
}

func NewRangedAttack(pawn, target PawnID, prerequisite ActionID) (RangedAttack, error) {
	if !validID(string(pawn)) || !validID(string(target)) || pawn == target {
		return RangedAttack{}, errors.New("ranged attack requires distinct valid pawn and target identities")
	}
	if !validID(string(prerequisite)) {
		return RangedAttack{}, errors.New("invalid ranged attack draft prerequisite")
	}
	return RangedAttack{pawn: pawn, target: target, draftAction: prerequisite}, nil
}

func (m RangedAttack) Pawn() PawnID          { return m.pawn }
func (m RangedAttack) Target() PawnID        { return m.target }
func (m RangedAttack) DraftAction() ActionID { return m.draftAction }

func NewRangedAttackAction(id ActionID, attack RangedAttack) (Action, error) {
	if !validID(string(id)) || id == attack.draftAction {
		return Action{}, errors.New("invalid ranged attack action identity or self prerequisite")
	}
	if _, err := NewRangedAttack(attack.pawn, attack.target, attack.draftAction); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RangedAttackAction, ranged: attack}, nil
}

func (a Action) RangedAttack() (RangedAttack, bool) { return a.ranged, a.kind == RangedAttackAction }
