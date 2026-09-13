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

// QuestFulfillSubmissionRequest is explicit player intent to fulfill one
// already-accepted quest's native settlement trade-request objective using
// one already-observed, already-visiting caravan. It mirrors
// SettlementGiftSubmissionRequest: this family is player-command-driven, not
// a fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type QuestFulfillSubmissionRequest struct {
	RequestID string
	World     World
	Fulfill   domain.QuestFulfill
}
type QuestFulfillSubmission struct {
	Request  QuestFulfillSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func (q QuestFulfillSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewQuestFulfill(q.Fulfill.Quest(), q.Fulfill.Caravan(), q.Fulfill.CrewIDs())
	return err
}

// SubmitQuestFulfill atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitSettlementGift uses. Submission
// neither acquires authority nor issues the native FulfillQuest command; a
// worker later admits and dispatches the committed action.
func (s *Store) SubmitQuestFulfill(ctx context.Context, q QuestFulfillSubmissionRequest) (QuestFulfillSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupQuestFulfillSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return QuestFulfillSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return QuestFulfillSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return QuestFulfillSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	result := QuestFulfillSubmission{Request: q, Plan: domain.PlanID("quest-fulfill-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("quest-fulfill-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewQuestFulfillAction(result.Action, q.Fulfill)
	if err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "quest_fulfill", q.World, result.Plan, result.Action); err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	data, err := json.Marshal(questFulfillPayload{q.Fulfill.Quest(), q.Fulfill.Caravan(), q.Fulfill.CrewIDs()})
	if err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO quest_fulfill_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return QuestFulfillSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return QuestFulfillSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupQuestFulfillSubmission(ctx context.Context, requestID string) (QuestFulfillSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return QuestFulfillSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupQuestFulfillSubmission(ctx, tx, requestID)
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return QuestFulfillSubmission{}, err
	}
	return result, nil
}
func lookupQuestFulfillSubmission(ctx context.Context, tx *sql.Tx, id string) (QuestFulfillSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "quest_fulfill")
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM quest_fulfill_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return QuestFulfillSubmission{}, ErrNotFound
	}
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	if len(data) > 32768 {
		return QuestFulfillSubmission{}, errors.New("quest fulfill submission exceeds bound")
	}
	var payload questFulfillPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return QuestFulfillSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return QuestFulfillSubmission{}, errors.New("noncanonical quest fulfill submission")
	}
	fulfill, err := domain.NewQuestFulfill(payload.Quest, payload.Caravan, payload.CrewIDs)
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	result := QuestFulfillSubmission{Request: QuestFulfillSubmissionRequest{RequestID: id, World: h.World, Fulfill: fulfill}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return QuestFulfillSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return QuestFulfillSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return QuestFulfillSubmission{}, errors.New("quest fulfill submission plan is corrupt")
	}
	actual, ok := actions[0].QuestFulfill()
	if !ok || actual != fulfill {
		return QuestFulfillSubmission{}, errors.New("quest fulfill submission differs from intent")
	}
	return result, nil
}
