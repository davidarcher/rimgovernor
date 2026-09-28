package domain

import "errors"

// Subdue is explicit intent to beat a colonist in an aggressive mental break
// down (#939: the PawnOrderIntent SUBDUE). It rides a preceding owned draft
// of the same pawn, which keeps the subduer drafted while the plan lives; it
// proves nothing about the target's eligibility or reachability.
type Subdue struct {
	pawn, target PawnID
	draftAction  ActionID
}

func NewSubdue(pawn, target PawnID, prerequisite ActionID) (Subdue, error) {
	if !validID(string(pawn)) || !validID(string(target)) || pawn == target {
		return Subdue{}, errors.New("subdue requires distinct valid pawn and target identities")
	}
	if !validID(string(prerequisite)) {
		return Subdue{}, errors.New("invalid subdue draft prerequisite")
	}
	return Subdue{pawn: pawn, target: target, draftAction: prerequisite}, nil
}

func (m Subdue) Pawn() PawnID          { return m.pawn }
func (m Subdue) Target() PawnID        { return m.target }
func (m Subdue) DraftAction() ActionID { return m.draftAction }

func NewSubdueAction(id ActionID, subdue Subdue) (Action, error) {
	if !validID(string(id)) || id == subdue.draftAction {
		return Action{}, errors.New("invalid subdue action identity or self prerequisite")
	}
	if _, err := NewSubdue(subdue.pawn, subdue.target, subdue.draftAction); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: SubdueAction, subdue: subdue}, nil
}

func (a Action) Subdue() (Subdue, bool) { return a.subdue, a.kind == SubdueAction }
