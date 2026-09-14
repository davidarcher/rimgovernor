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

// HusbandrySubmissionRequest is explicit player intent to write one
// already-observed animal's training request or slaughter designation. It
// mirrors TendSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type HusbandrySubmissionRequest struct {
	RequestID string
	World     World
	Husbandry domain.Husbandry
}
type HusbandrySubmission struct {
	Request  HusbandrySubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}
type husbandryPayload struct {
	Animal       domain.PawnID
	Method       domain.HusbandryMethod
	TrainableDef string
}

func (q HusbandrySubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewHusbandry(q.Husbandry.Animal(), q.Husbandry.Method(), q.Husbandry.TrainableDef())
	return err
}

// SubmitHusbandry atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitTend uses.
func (s *Store) SubmitHusbandry(ctx context.Context, q HusbandrySubmissionRequest) (HusbandrySubmission, bool, error) {
	if err := q.validate(); err != nil {
		return HusbandrySubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return HusbandrySubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupHusbandrySubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return HusbandrySubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return HusbandrySubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return HusbandrySubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return HusbandrySubmission{}, false, err
	}
	result := HusbandrySubmission{Request: q, Plan: domain.PlanID("husbandry-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("husbandry-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewHusbandryAction(result.Action, q.Husbandry)
	if err != nil {
		return HusbandrySubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return HusbandrySubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return HusbandrySubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "husbandry", q.World, result.Plan, result.Action); err != nil {
		return HusbandrySubmission{}, false, err
	}
	data, err := json.Marshal(husbandryPayload{q.Husbandry.Animal(), q.Husbandry.Method(), q.Husbandry.TrainableDef()})
	if err != nil {
		return HusbandrySubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO husbandry_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return HusbandrySubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return HusbandrySubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupHusbandrySubmission(ctx context.Context, requestID string) (HusbandrySubmission, error) {
	if err := submissionID(requestID); err != nil {
		return HusbandrySubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return HusbandrySubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupHusbandrySubmission(ctx, tx, requestID)
	if err != nil {
		return HusbandrySubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return HusbandrySubmission{}, err
	}
	return result, nil
}
func lookupHusbandrySubmission(ctx context.Context, tx *sql.Tx, id string) (HusbandrySubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "husbandry")
	if err != nil {
		return HusbandrySubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM husbandry_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return HusbandrySubmission{}, ErrNotFound
	}
	if err != nil {
		return HusbandrySubmission{}, err
	}
	if len(data) > 32768 {
		return HusbandrySubmission{}, errors.New("husbandry submission exceeds bound")
	}
	var payload husbandryPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return HusbandrySubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return HusbandrySubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return HusbandrySubmission{}, errors.New("noncanonical husbandry submission")
	}
	husbandry, err := domain.NewHusbandry(payload.Animal, payload.Method, payload.TrainableDef)
	if err != nil {
		return HusbandrySubmission{}, err
	}
	result := HusbandrySubmission{Request: HusbandrySubmissionRequest{RequestID: id, World: h.World, Husbandry: husbandry}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return HusbandrySubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return HusbandrySubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return HusbandrySubmission{}, errors.New("husbandry submission plan is corrupt")
	}
	actual, ok := actions[0].Husbandry()
	if !ok || actual != husbandry {
		return HusbandrySubmission{}, errors.New("husbandry submission differs from intent")
	}
	return result, nil
}
