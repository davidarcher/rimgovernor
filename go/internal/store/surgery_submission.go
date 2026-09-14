package store

import (
	"bytes"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SurgerySubmissionRequest is explicit player intent to queue one exact
// native medical operation on one already-observed living patient. It
// mirrors TendSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type SurgerySubmissionRequest struct {
	RequestID string
	World     World
	Surgery   domain.Surgery
}
type SurgerySubmission struct {
	Request  SurgerySubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}
type surgeryPayload struct {
	Patient domain.PawnID
	Recipe  string
	Part    int32
}

func (q SurgerySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewSurgery(q.Surgery.Patient(), q.Surgery.Recipe(), q.Surgery.Part())
	return err
}

// SubmitSurgery atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitTend uses.
func (s *Store) SubmitSurgery(ctx context.Context, q SurgerySubmissionRequest) (SurgerySubmission, bool, error) {
	if err := q.validate(); err != nil {
		return SurgerySubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return SurgerySubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupSurgerySubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return SurgerySubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return SurgerySubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return SurgerySubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return SurgerySubmission{}, false, err
	}
	result := SurgerySubmission{Request: q, Plan: domain.PlanID("surgery-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("surgery-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewSurgeryAction(result.Action, q.Surgery)
	if err != nil {
		return SurgerySubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return SurgerySubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return SurgerySubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "surgery", q.World, result.Plan, result.Action); err != nil {
		return SurgerySubmission{}, false, err
	}
	data, err := json.Marshal(surgeryPayload{q.Surgery.Patient(), q.Surgery.Recipe(), q.Surgery.Part()})
	if err != nil {
		return SurgerySubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO surgery_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return SurgerySubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return SurgerySubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupSurgerySubmission(ctx context.Context, requestID string) (SurgerySubmission, error) {
	if err := submissionID(requestID); err != nil {
		return SurgerySubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return SurgerySubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupSurgerySubmission(ctx, tx, requestID)
	if err != nil {
		return SurgerySubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return SurgerySubmission{}, err
	}
	return result, nil
}
func lookupSurgerySubmission(ctx context.Context, tx *sql.Tx, id string) (SurgerySubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "surgery")
	if err != nil {
		return SurgerySubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM surgery_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return SurgerySubmission{}, ErrNotFound
	}
	if err != nil {
		return SurgerySubmission{}, err
	}
	if len(data) > 32768 {
		return SurgerySubmission{}, errors.New("surgery submission exceeds bound")
	}
	var payload surgeryPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return SurgerySubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return SurgerySubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return SurgerySubmission{}, errors.New("noncanonical surgery submission")
	}
	surgery, err := domain.NewSurgery(payload.Patient, payload.Recipe, payload.Part)
	if err != nil {
		return SurgerySubmission{}, err
	}
	result := SurgerySubmission{Request: SurgerySubmissionRequest{RequestID: id, World: h.World, Surgery: surgery}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return SurgerySubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return SurgerySubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return SurgerySubmission{}, errors.New("surgery submission plan is corrupt")
	}
	actual, ok := actions[0].Surgery()
	if !ok || actual != surgery {
		return SurgerySubmission{}, errors.New("surgery submission differs from intent")
	}
	return result, nil
}
