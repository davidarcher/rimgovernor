package domain

import "errors"

type PawnID string
type ControllerSessionID string

// OwnedDraft drafts one pawn for the plan that holds it (#939: a DraftIntent
// with drafted=true). The draft is the plan's: the combat planner undrafts
// any drafted colonist no live plan needs, so there is no native claim and
// no per-action cleanup.
type OwnedDraft struct{ pawn PawnID }

func NewOwnedDraft(pawn PawnID) (OwnedDraft, error) {
	if !validID(string(pawn)) {
		return OwnedDraft{}, errors.New("invalid draft pawn")
	}
	return OwnedDraft{pawn}, nil
}
func (d OwnedDraft) Pawn() PawnID { return d.pawn }
func NewOwnedDraftAction(id ActionID, draft OwnedDraft) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewOwnedDraft(draft.pawn); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: OwnedDraftAction, draft: draft}, nil
}
func (a Action) OwnedDraft() (OwnedDraft, bool) { return a.draft, a.kind == OwnedDraftAction }
