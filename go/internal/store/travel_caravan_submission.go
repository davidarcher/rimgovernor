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

// TravelCaravanSubmissionRequest is explicit player intent to route or hold
// one already-observed, already-formed player caravan. It mirrors
// CaravanDepartureSubmissionRequest/QuestFulfillSubmissionRequest: this
// family is player-command-driven, not a fixed-priority routine producer,
// so a submitted request commits its own one-action plan immediately
// instead of being composed by a routine planner.
type TravelCaravanSubmissionRequest struct {
	RequestID string
	World     World
	Travel    domain.TravelCaravan
}
type TravelCaravanSubmission struct {
	Request  TravelCaravanSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func (q TravelCaravanSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewTravelCaravan(q.Travel.Caravan(), q.Travel.Kind(), q.Travel.DestinationTile())
	return err
}

// SubmitTravelCaravan atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitQuestFulfill/SubmitCaravanDeparture
// use. Submission neither acquires authority nor issues the native
// TravelCaravan command; a worker later admits and dispatches the committed
// action.
func (s *Store) SubmitTravelCaravan(ctx context.Context, q TravelCaravanSubmissionRequest) (TravelCaravanSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupTravelCaravanSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return TravelCaravanSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return TravelCaravanSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return TravelCaravanSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	result := TravelCaravanSubmission{Request: q, Plan: domain.PlanID("travel-caravan-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("travel-caravan-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewTravelCaravanAction(result.Action, q.Travel)
	if err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "travel_caravan", q.World, result.Plan, result.Action); err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	data, err := json.Marshal(travelCaravanPayload{q.Travel.Caravan(), q.Travel.Kind(), q.Travel.DestinationTile()})
	if err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO travel_caravan_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return TravelCaravanSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return TravelCaravanSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupTravelCaravanSubmission(ctx context.Context, requestID string) (TravelCaravanSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return TravelCaravanSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupTravelCaravanSubmission(ctx, tx, requestID)
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return TravelCaravanSubmission{}, err
	}
	return result, nil
}
func lookupTravelCaravanSubmission(ctx context.Context, tx *sql.Tx, id string) (TravelCaravanSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "travel_caravan")
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM travel_caravan_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return TravelCaravanSubmission{}, ErrNotFound
	}
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	if len(data) > 32768 {
		return TravelCaravanSubmission{}, errors.New("travel caravan submission exceeds bound")
	}
	var payload travelCaravanPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return TravelCaravanSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return TravelCaravanSubmission{}, errors.New("noncanonical travel caravan submission")
	}
	travel, err := domain.NewTravelCaravan(payload.Caravan, payload.Kind, payload.DestinationTile)
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	result := TravelCaravanSubmission{Request: TravelCaravanSubmissionRequest{RequestID: id, World: h.World, Travel: travel}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return TravelCaravanSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return TravelCaravanSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return TravelCaravanSubmission{}, errors.New("travel caravan submission plan is corrupt")
	}
	actual, ok := actions[0].TravelCaravan()
	if !ok || actual != travel {
		return TravelCaravanSubmission{}, errors.New("travel caravan submission differs from intent")
	}
	return result, nil
}
