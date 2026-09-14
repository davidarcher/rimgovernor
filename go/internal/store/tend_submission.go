package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"

	"crypto/rand"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TendSubmissionRequest is explicit player intent to have one already-observed
// doctor tend one already-observed patient. It mirrors
// ResearchSelectSubmissionRequest: this family is player-command-driven, not
// a fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type TendSubmissionRequest struct {
	RequestID string
	World     World
	Tend      domain.Tend
}
type TendSubmission struct {
	Request  TendSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type tendPayload struct {
	Doctor  domain.PawnID
	Patient domain.PawnID
}

func (t TendSubmissionRequest) validate() error {
	if err := submissionID(t.RequestID); err != nil {
		return err
	}
	if err := t.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewTend(t.Tend.Doctor(), t.Tend.Patient())
	return err
}

// SubmitTend atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitResearchSelect uses. Submission
// neither acquires authority nor issues the native Tend command; a worker
// later admits and dispatches the committed action.
func (s *Store) SubmitTend(ctx context.Context, t TendSubmissionRequest) (TendSubmission, bool, error) {
	if err := t.validate(); err != nil {
		return TendSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TendSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupTendSubmission(ctx, tx, t.RequestID)
	if err == nil {
		if old.Request != t {
			return TendSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return TendSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return TendSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return TendSubmission{}, false, err
	}
	result := TendSubmission{Request: t, Plan: domain.PlanID("tend-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("tend-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewTendAction(result.Action, t.Tend)
	if err != nil {
		return TendSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return TendSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return TendSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, t.RequestID, "tend", t.World, result.Plan, result.Action); err != nil {
		return TendSubmission{}, false, err
	}
	data, err := json.Marshal(tendPayload{t.Tend.Doctor(), t.Tend.Patient()})
	if err != nil {
		return TendSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO tend_submissions(request_id,payload) VALUES(?,?)", t.RequestID, data); err != nil {
		return TendSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return TendSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupTendSubmission(ctx context.Context, requestID string) (TendSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return TendSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TendSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupTendSubmission(ctx, tx, requestID)
	if err != nil {
		return TendSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return TendSubmission{}, err
	}
	return result, nil
}
func lookupTendSubmission(ctx context.Context, tx *sql.Tx, id string) (TendSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "tend")
	if err != nil {
		return TendSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM tend_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return TendSubmission{}, ErrNotFound
	}
	if err != nil {
		return TendSubmission{}, err
	}
	if len(data) > 32768 {
		return TendSubmission{}, errors.New("tend submission exceeds bound")
	}
	var payload tendPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return TendSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return TendSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return TendSubmission{}, errors.New("noncanonical tend submission")
	}
	tend, err := domain.NewTend(payload.Doctor, payload.Patient)
	if err != nil {
		return TendSubmission{}, err
	}
	result := TendSubmission{Request: TendSubmissionRequest{RequestID: id, World: h.World, Tend: tend}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return TendSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return TendSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return TendSubmission{}, errors.New("tend submission plan is corrupt")
	}
	actual, ok := actions[0].Tend()
	if !ok || actual != tend {
		return TendSubmission{}, errors.New("tend submission differs from intent")
	}
	return result, nil
}
