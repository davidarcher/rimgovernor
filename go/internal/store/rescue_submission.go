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

// RescueSubmissionRequest is explicit player intent to have one
// already-observed rescuer rescue one already-observed patient. It mirrors
// TendSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type RescueSubmissionRequest struct {
	RequestID string
	World     World
	Rescue    domain.Rescue
}
type RescueSubmission struct {
	Request  RescueSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type rescuePayload struct {
	Rescuer domain.PawnID
	Patient domain.PawnID
}

func (r RescueSubmissionRequest) validate() error {
	if err := submissionID(r.RequestID); err != nil {
		return err
	}
	if err := r.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewRescue(r.Rescue.Rescuer(), r.Rescue.Patient())
	return err
}

// SubmitRescue atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitTend uses. Submission neither
// acquires authority nor issues the native Rescue command; a worker later
// admits and dispatches the committed action.
func (s *Store) SubmitRescue(ctx context.Context, r RescueSubmissionRequest) (RescueSubmission, bool, error) {
	if err := r.validate(); err != nil {
		return RescueSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RescueSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupRescueSubmission(ctx, tx, r.RequestID)
	if err == nil {
		if old.Request != r {
			return RescueSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return RescueSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return RescueSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return RescueSubmission{}, false, err
	}
	result := RescueSubmission{Request: r, Plan: domain.PlanID("rescue-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("rescue-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewRescueAction(result.Action, r.Rescue)
	if err != nil {
		return RescueSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return RescueSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return RescueSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, r.RequestID, "rescue", r.World, result.Plan, result.Action); err != nil {
		return RescueSubmission{}, false, err
	}
	data, err := json.Marshal(rescuePayload{r.Rescue.Rescuer(), r.Rescue.Patient()})
	if err != nil {
		return RescueSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO rescue_submissions(request_id,payload) VALUES(?,?)", r.RequestID, data); err != nil {
		return RescueSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return RescueSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupRescueSubmission(ctx context.Context, requestID string) (RescueSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return RescueSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RescueSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupRescueSubmission(ctx, tx, requestID)
	if err != nil {
		return RescueSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return RescueSubmission{}, err
	}
	return result, nil
}
func lookupRescueSubmission(ctx context.Context, tx *sql.Tx, id string) (RescueSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "rescue")
	if err != nil {
		return RescueSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM rescue_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return RescueSubmission{}, ErrNotFound
	}
	if err != nil {
		return RescueSubmission{}, err
	}
	if len(data) > 32768 {
		return RescueSubmission{}, errors.New("rescue submission exceeds bound")
	}
	var payload rescuePayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return RescueSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return RescueSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return RescueSubmission{}, errors.New("noncanonical rescue submission")
	}
	rescue, err := domain.NewRescue(payload.Rescuer, payload.Patient)
	if err != nil {
		return RescueSubmission{}, err
	}
	result := RescueSubmission{Request: RescueSubmissionRequest{RequestID: id, World: h.World, Rescue: rescue}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return RescueSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return RescueSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return RescueSubmission{}, errors.New("rescue submission plan is corrupt")
	}
	actual, ok := actions[0].Rescue()
	if !ok || actual != rescue {
		return RescueSubmission{}, errors.New("rescue submission differs from intent")
	}
	return result, nil
}
