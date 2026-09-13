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

// CaravanDepartureSubmissionRequest is explicit player intent to form and
// send one already-selected crew/cargo toward one already-scouted world
// tile. It mirrors SubmissionRequest/DraftSubmissionRequest: this family is
// player-command-driven, not a fixed-priority routine producer, so a
// submitted request commits its own one-action plan immediately instead of
// being composed by a routine planner.
type CaravanDepartureSubmissionRequest struct {
	RequestID string
	World     World
	Departure domain.CaravanDeparture
}
type CaravanDepartureSubmission struct {
	Request  CaravanDepartureSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func (q CaravanDepartureSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewCaravanDeparture(q.Departure.Crew(), q.Departure.Cargo(), q.Departure.DestinationTile())
	return err
}

// SubmitCaravanDeparture atomically stores one explicit player intent and
// its one-action plan, the same shape SubmitBuilding/SubmitDraft use.
// Submission neither acquires authority nor issues the native FormCaravan
// command; a worker later admits and dispatches the committed action.
func (s *Store) SubmitCaravanDeparture(ctx context.Context, q CaravanDepartureSubmissionRequest) (CaravanDepartureSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupCaravanDepartureSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return CaravanDepartureSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return CaravanDepartureSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return CaravanDepartureSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	result := CaravanDepartureSubmission{Request: q, Plan: domain.PlanID("caravan-departure-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("caravan-departure-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewCaravanDepartureAction(result.Action, q.Departure)
	if err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "caravan_departure", q.World, result.Plan, result.Action); err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	data, err := json.Marshal(caravanPayload{q.Departure.Crew(), q.Departure.Cargo(), q.Departure.DestinationTile()})
	if err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO caravan_departure_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return CaravanDepartureSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return CaravanDepartureSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupCaravanDepartureSubmission(ctx context.Context, requestID string) (CaravanDepartureSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return CaravanDepartureSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupCaravanDepartureSubmission(ctx, tx, requestID)
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return CaravanDepartureSubmission{}, err
	}
	return result, nil
}
func lookupCaravanDepartureSubmission(ctx context.Context, tx *sql.Tx, id string) (CaravanDepartureSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "caravan_departure")
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM caravan_departure_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return CaravanDepartureSubmission{}, ErrNotFound
	}
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	if len(data) > 32768 {
		return CaravanDepartureSubmission{}, errors.New("caravan departure submission exceeds bound")
	}
	var payload caravanPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return CaravanDepartureSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return CaravanDepartureSubmission{}, errors.New("noncanonical caravan departure submission")
	}
	departure, err := domain.NewCaravanDeparture(payload.Crew, payload.Cargo, payload.DestinationTile)
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	result := CaravanDepartureSubmission{Request: CaravanDepartureSubmissionRequest{RequestID: id, World: h.World, Departure: departure}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return CaravanDepartureSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return CaravanDepartureSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return CaravanDepartureSubmission{}, errors.New("caravan departure submission plan is corrupt")
	}
	actual, ok := actions[0].CaravanDeparture()
	if !ok || actual != departure {
		return CaravanDepartureSubmission{}, errors.New("caravan departure submission differs from intent")
	}
	return result, nil
}
