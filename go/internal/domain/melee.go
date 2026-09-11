package domain

import "errors"

// MeleeAttack is explicit intent. The draft prerequisite does not prove current
// ownership, target eligibility, reachability or permission to dispatch a job.
type MeleeAttack struct {
	pawn, target PawnID
	draftAction  ActionID
}

func NewMeleeAttack(pawn, target PawnID, prerequisite ActionID) (MeleeAttack, error) {
	if !validID(string(pawn)) || !validID(string(target)) || pawn == target {
		return MeleeAttack{}, errors.New("melee requires distinct valid pawn and target identities")
	}
	if !validID(string(prerequisite)) {
		return MeleeAttack{}, errors.New("invalid melee draft prerequisite")
	}
	return MeleeAttack{pawn: pawn, target: target, draftAction: prerequisite}, nil
}

func (m MeleeAttack) Pawn() PawnID          { return m.pawn }
func (m MeleeAttack) Target() PawnID        { return m.target }
func (m MeleeAttack) DraftAction() ActionID { return m.draftAction }

func NewMeleeAttackAction(id ActionID, attack MeleeAttack) (Action, error) {
	if !validID(string(id)) || id == attack.draftAction {
		return Action{}, errors.New("invalid melee action identity or self prerequisite")
	}
	if _, err := NewMeleeAttack(attack.pawn, attack.target, attack.draftAction); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: MeleeAttackAction, melee: attack}, nil
}

func (a Action) MeleeAttack() (MeleeAttack, bool) { return a.melee, a.kind == MeleeAttackAction }
