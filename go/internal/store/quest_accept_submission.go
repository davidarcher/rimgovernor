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

// QuestAcceptSubmissionRequest is explicit player intent to accept one
// already-observed quest offer with one already-chosen accepter pawn and
// reward choice. It mirrors CaravanDepartureSubmissionRequest: this family is
// player-command-driven, not a fixed-priority routine producer, so a
// submitted request commits its own one-action plan immediately instead of
// being composed by a routine planner.
type QuestAcceptSubmissionRequest struct {
	RequestID string
	World     World
	Accept    domain.QuestAccept
}
type QuestAcceptSubmission struct {
	Request  QuestAcceptSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type questAcceptPayload struct {
	Quest        domain.QuestID
	AccepterPawn domain.PawnID
	RewardChoice int32
}

func (q QuestAcceptSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewQuestAccept(q.Accept.Quest(), q.Accept.AccepterPawn(), q.Accept.RewardChoice())
	return err
}

// SubmitQuestAccept atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitCaravanDeparture uses. Submission
// neither acquires authority nor issues the native AcceptQuest command; a
// worker later admits and dispatches the committed action.
func (s *Store) SubmitQuestAccept(ctx context.Context, q QuestAcceptSubmissionRequest) (QuestAcceptSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupQuestAcceptSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return QuestAcceptSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return QuestAcceptSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return QuestAcceptSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	result := QuestAcceptSubmission{Request: q, Plan: domain.PlanID("quest-accept-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("quest-accept-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewQuestAcceptAction(result.Action, q.Accept)
	if err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "quest_accept", q.World, result.Plan, result.Action); err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	data, err := json.Marshal(questAcceptPayload{q.Accept.Quest(), q.Accept.AccepterPawn(), q.Accept.RewardChoice()})
	if err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO quest_accept_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return QuestAcceptSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return QuestAcceptSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupQuestAcceptSubmission(ctx context.Context, requestID string) (QuestAcceptSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return QuestAcceptSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupQuestAcceptSubmission(ctx, tx, requestID)
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return QuestAcceptSubmission{}, err
	}
	return result, nil
}
func lookupQuestAcceptSubmission(ctx context.Context, tx *sql.Tx, id string) (QuestAcceptSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "quest_accept")
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM quest_accept_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return QuestAcceptSubmission{}, ErrNotFound
	}
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	if len(data) > 32768 {
		return QuestAcceptSubmission{}, errors.New("quest accept submission exceeds bound")
	}
	var payload questAcceptPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return QuestAcceptSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return QuestAcceptSubmission{}, errors.New("noncanonical quest accept submission")
	}
	accept, err := domain.NewQuestAccept(payload.Quest, payload.AccepterPawn, payload.RewardChoice)
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	result := QuestAcceptSubmission{Request: QuestAcceptSubmissionRequest{RequestID: id, World: h.World, Accept: accept}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return QuestAcceptSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return QuestAcceptSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return QuestAcceptSubmission{}, errors.New("quest accept submission plan is corrupt")
	}
	actual, ok := actions[0].QuestAccept()
	if !ok || actual != accept {
		return QuestAcceptSubmission{}, errors.New("quest accept submission differs from intent")
	}
	return result, nil
}
