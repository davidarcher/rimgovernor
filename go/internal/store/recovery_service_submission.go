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

// RecoveryServiceSubmissionRequest is explicit player intent to send one
// already-observed undrafted pawn to repair, restore or refuel one
// already-observed building. It mirrors TendSubmissionRequest: this family
// is player-command-driven, not a fixed-priority routine producer, so a
// submitted request commits its own one-action plan immediately instead of
// being composed by a routine planner.
type RecoveryServiceSubmissionRequest struct {
	RequestID string
	World     World
	Service   domain.RecoveryService
}
type RecoveryServiceSubmission struct {
	Request  RecoveryServiceSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}
type recoveryServicePayload struct {
	Pawn   domain.PawnID
	Thing  string
	Method domain.RecoveryMethod
}

func (q RecoveryServiceSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewRecoveryService(q.Service.Pawn(), q.Service.Thing(), q.Service.Method())
	return err
}

// SubmitRecoveryService atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitTend uses.
func (s *Store) SubmitRecoveryService(ctx context.Context, q RecoveryServiceSubmissionRequest) (RecoveryServiceSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupRecoveryServiceSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return RecoveryServiceSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return RecoveryServiceSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return RecoveryServiceSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	result := RecoveryServiceSubmission{Request: q, Plan: domain.PlanID("recovery-service-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("recovery-service-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewRecoveryServiceAction(result.Action, q.Service)
	if err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "recovery_service", q.World, result.Plan, result.Action); err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	data, err := json.Marshal(recoveryServicePayload{q.Service.Pawn(), q.Service.Thing(), q.Service.Method()})
	if err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO recovery_service_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return RecoveryServiceSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return RecoveryServiceSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupRecoveryServiceSubmission(ctx context.Context, requestID string) (RecoveryServiceSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return RecoveryServiceSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupRecoveryServiceSubmission(ctx, tx, requestID)
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return RecoveryServiceSubmission{}, err
	}
	return result, nil
}
func lookupRecoveryServiceSubmission(ctx context.Context, tx *sql.Tx, id string) (RecoveryServiceSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "recovery_service")
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM recovery_service_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return RecoveryServiceSubmission{}, ErrNotFound
	}
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	if len(data) > 32768 {
		return RecoveryServiceSubmission{}, errors.New("recovery service submission exceeds bound")
	}
	var payload recoveryServicePayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return RecoveryServiceSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return RecoveryServiceSubmission{}, errors.New("noncanonical recovery service submission")
	}
	service, err := domain.NewRecoveryService(payload.Pawn, payload.Thing, payload.Method)
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	result := RecoveryServiceSubmission{Request: RecoveryServiceSubmissionRequest{RequestID: id, World: h.World, Service: service}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return RecoveryServiceSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return RecoveryServiceSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return RecoveryServiceSubmission{}, errors.New("recovery service submission plan is corrupt")
	}
	actual, ok := actions[0].RecoveryService()
	if !ok || actual != service {
		return RecoveryServiceSubmission{}, errors.New("recovery service submission differs from intent")
	}
	return result, nil
}
