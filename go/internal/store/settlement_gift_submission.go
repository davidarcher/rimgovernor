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

// SettlementGiftSubmissionRequest is explicit player intent to gift an exact
// silver amount from one already-observed, already-visiting caravan to the
// exact faction of the settlement it currently sits at. It mirrors
// CaravanDepartureSubmissionRequest: this family is player-command-driven,
// not a fixed-priority routine producer, so a submitted request commits its
// own one-action plan immediately instead of being composed by a routine
// planner.
type SettlementGiftSubmissionRequest struct {
	RequestID string
	World     World
	Gift      domain.SettlementGift
}
type SettlementGiftSubmission struct {
	Request  SettlementGiftSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func (q SettlementGiftSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewSettlementGift(q.Gift.Caravan(), q.Gift.Settlement(), q.Gift.Faction(), q.Gift.CrewIDs(), q.Gift.Silver())
	return err
}

// SubmitSettlementGift atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitCaravanDeparture uses. Submission
// neither acquires authority nor issues the native GiftCaravanSilver command;
// a worker later admits and dispatches the committed action.
func (s *Store) SubmitSettlementGift(ctx context.Context, q SettlementGiftSubmissionRequest) (SettlementGiftSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupSettlementGiftSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return SettlementGiftSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return SettlementGiftSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return SettlementGiftSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	result := SettlementGiftSubmission{Request: q, Plan: domain.PlanID("settlement-gift-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("settlement-gift-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewSettlementGiftAction(result.Action, q.Gift)
	if err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "settlement_gift", q.World, result.Plan, result.Action); err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	data, err := json.Marshal(settlementGiftPayload{q.Gift.Caravan(), q.Gift.Settlement(), q.Gift.Faction(), q.Gift.CrewIDs(), q.Gift.Silver()})
	if err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO settlement_gift_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return SettlementGiftSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return SettlementGiftSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupSettlementGiftSubmission(ctx context.Context, requestID string) (SettlementGiftSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return SettlementGiftSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupSettlementGiftSubmission(ctx, tx, requestID)
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return SettlementGiftSubmission{}, err
	}
	return result, nil
}
func lookupSettlementGiftSubmission(ctx context.Context, tx *sql.Tx, id string) (SettlementGiftSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "settlement_gift")
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM settlement_gift_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return SettlementGiftSubmission{}, ErrNotFound
	}
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	if len(data) > 32768 {
		return SettlementGiftSubmission{}, errors.New("settlement gift submission exceeds bound")
	}
	var payload settlementGiftPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return SettlementGiftSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return SettlementGiftSubmission{}, errors.New("noncanonical settlement gift submission")
	}
	gift, err := domain.NewSettlementGift(payload.Caravan, payload.Settlement, payload.Faction, payload.CrewIDs, payload.Silver)
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	result := SettlementGiftSubmission{Request: SettlementGiftSubmissionRequest{RequestID: id, World: h.World, Gift: gift}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return SettlementGiftSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return SettlementGiftSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return SettlementGiftSubmission{}, errors.New("settlement gift submission plan is corrupt")
	}
	actual, ok := actions[0].SettlementGift()
	if !ok || actual != gift {
		return SettlementGiftSubmission{}, errors.New("settlement gift submission differs from intent")
	}
	return result, nil
}
