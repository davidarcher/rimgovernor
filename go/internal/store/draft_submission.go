package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type DraftSubmissionRequest struct {
	RequestID string
	World     World
	Draft     domain.OwnedDraft
}
type DraftSubmission struct {
	Request  DraftSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func (q DraftSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewOwnedDraft(q.Draft.Pawn())
	return err
}

// SubmitDraft atomically stores one explicit player intent and its action.
func (s *Store) SubmitDraft(ctx context.Context, q DraftSubmissionRequest) (DraftSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return DraftSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return DraftSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupDraftSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return DraftSubmission{}, false, ErrConflict
		}
		err = tx.Commit()
		return old, false, err
	}
	if !errors.Is(err, ErrNotFound) {
		return DraftSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return DraftSubmission{}, false, err
	}
	result := DraftSubmission{Request: q, Plan: domain.PlanID("draft-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("draft-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	a, err := domain.NewOwnedDraftAction(result.Action, q.Draft)
	if err != nil {
		return DraftSubmission{}, false, err
	}
	p, err := domain.NewPlan(result.Plan, 1, []domain.Action{a})
	if err != nil {
		return DraftSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, p); err != nil {
		return DraftSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "owned_draft", q.World, result.Plan, result.Action); err != nil {
		return DraftSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO draft_submissions(request_id,pawn) VALUES(?,?)", q.RequestID, q.Draft.Pawn()); err != nil {
		return DraftSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return DraftSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupDraftSubmission(ctx context.Context, id string) (DraftSubmission, error) {
	if err := submissionID(id); err != nil {
		return DraftSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return DraftSubmission{}, err
	}
	defer tx.Rollback()
	v, err := lookupDraftSubmission(ctx, tx, id)
	if err != nil {
		return DraftSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return DraftSubmission{}, err
	}
	return v, nil
}
func lookupDraftSubmission(ctx context.Context, tx *sql.Tx, id string) (DraftSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "owned_draft")
	if err != nil {
		return DraftSubmission{}, err
	}
	var pawn domain.PawnID
	if err = tx.QueryRowContext(ctx, "SELECT pawn FROM draft_submissions WHERE request_id=?", id).Scan(&pawn); err != nil {
		return DraftSubmission{}, err
	}
	d, err := domain.NewOwnedDraft(pawn)
	if err != nil {
		return DraftSubmission{}, err
	}
	v := DraftSubmission{Request: DraftSubmissionRequest{RequestID: id, World: h.World, Draft: d}, Plan: h.Plan, Action: h.Action, Revision: 1}
	if err = v.Request.validate(); err != nil {
		return DraftSubmission{}, err
	}
	state, err := load(ctx, tx, h.Plan)
	if err != nil {
		return DraftSubmission{}, err
	}
	a := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(a) != 1 || a[0].ID() != h.Action {
		return DraftSubmission{}, errors.New("draft submission plan corrupt")
	}
	actual, ok := a[0].OwnedDraft()
	if !ok || actual != d {
		return DraftSubmission{}, errors.New("draft submission differs from intent")
	}
	return v, nil
}
