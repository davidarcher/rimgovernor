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

// MovementSubmissionRequest is explicit player intent to walk one
// already-observed pawn to one already-observed cell. Unlike TendSubmission
// and its siblings, a movement submission commits a two-action plan: an
// OwnedDraft action followed by the Movement action that depends on it, the
// same shape interpreter.moveActions builds. Movement layers on an owned
// draft exactly like MeleeAttack and RangedAttack, so admitting the player's
// intent requires acquiring the draft first.
type MovementSubmissionRequest struct {
	RequestID   string
	World       World
	Pawn        domain.PawnID
	Destination domain.Cell
}
type MovementSubmission struct {
	Request     MovementSubmissionRequest
	Plan        domain.PlanID
	DraftAction domain.ActionID
	Action      domain.ActionID
	Revision    domain.PlanRevision
}
type movementPayload struct {
	Pawn domain.PawnID
	X    int32
	Z    int32
}

func (q MovementSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	if _, err := domain.NewOwnedDraft(q.Pawn); err != nil {
		return err
	}
	if q.Destination.X < 0 || q.Destination.Z < 0 {
		return errors.New("movement destination must be nonnegative")
	}
	return nil
}

// SubmitMovement atomically stores one explicit player intent and its
// two-action plan (draft, then movement).
func (s *Store) SubmitMovement(ctx context.Context, q MovementSubmissionRequest) (MovementSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return MovementSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return MovementSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupMovementSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return MovementSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return MovementSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return MovementSubmission{}, false, err
	}
	var entropy [48]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return MovementSubmission{}, false, err
	}
	result := MovementSubmission{
		Request:     q,
		Plan:        domain.PlanID("movement-" + hex.EncodeToString(entropy[:16])),
		DraftAction: domain.ActionID("movement-draft-action-" + hex.EncodeToString(entropy[16:32])),
		Action:      domain.ActionID("movement-action-" + hex.EncodeToString(entropy[32:])),
		Revision:    1,
	}
	draft, err := domain.NewOwnedDraft(q.Pawn)
	if err != nil {
		return MovementSubmission{}, false, err
	}
	draftAction, err := domain.NewOwnedDraftAction(result.DraftAction, draft)
	if err != nil {
		return MovementSubmission{}, false, err
	}
	movement, err := domain.NewMovement(q.Pawn, q.Destination, result.DraftAction)
	if err != nil {
		return MovementSubmission{}, false, err
	}
	movementAction, err := domain.NewMovementAction(result.Action, movement)
	if err != nil {
		return MovementSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{draftAction, movementAction})
	if err != nil {
		return MovementSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return MovementSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "movement", q.World, result.Plan, result.Action); err != nil {
		return MovementSubmission{}, false, err
	}
	data, err := json.Marshal(movementPayload{q.Pawn, q.Destination.X, q.Destination.Z})
	if err != nil {
		return MovementSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO movement_submissions(request_id,draft_action,payload) VALUES(?,?,?)", q.RequestID, result.DraftAction, data); err != nil {
		return MovementSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return MovementSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupMovementSubmission(ctx context.Context, requestID string) (MovementSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return MovementSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return MovementSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupMovementSubmission(ctx, tx, requestID)
	if err != nil {
		return MovementSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return MovementSubmission{}, err
	}
	return result, nil
}
func lookupMovementSubmission(ctx context.Context, tx *sql.Tx, id string) (MovementSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "movement")
	if err != nil {
		return MovementSubmission{}, err
	}
	var draftAction domain.ActionID
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT draft_action,payload FROM movement_submissions WHERE request_id=?", id).Scan(&draftAction, &data)
	if errors.Is(err, sql.ErrNoRows) {
		return MovementSubmission{}, ErrNotFound
	}
	if err != nil {
		return MovementSubmission{}, err
	}
	if len(data) > 32768 {
		return MovementSubmission{}, errors.New("movement submission exceeds bound")
	}
	var payload movementPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return MovementSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return MovementSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return MovementSubmission{}, errors.New("noncanonical movement submission")
	}
	destination := domain.Cell{X: payload.X, Z: payload.Z}
	result := MovementSubmission{
		Request:     MovementSubmissionRequest{RequestID: id, World: h.World, Pawn: payload.Pawn, Destination: destination},
		Revision:    h.Revision,
		Plan:        h.Plan,
		DraftAction: draftAction,
		Action:      h.Action,
	}
	if err = result.Request.validate(); err != nil {
		return MovementSubmission{}, err
	}
	draft, err := domain.NewOwnedDraft(payload.Pawn)
	if err != nil {
		return MovementSubmission{}, err
	}
	movement, err := domain.NewMovement(payload.Pawn, destination, draftAction)
	if err != nil {
		return MovementSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return MovementSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 2 || actions[0].ID() != draftAction || actions[1].ID() != result.Action {
		return MovementSubmission{}, errors.New("movement submission plan is corrupt")
	}
	actualDraft, ok := actions[0].OwnedDraft()
	if !ok || actualDraft != draft {
		return MovementSubmission{}, errors.New("movement submission draft differs from intent")
	}
	actualMovement, ok := actions[1].Movement()
	if !ok || actualMovement != movement {
		return MovementSubmission{}, errors.New("movement submission differs from intent")
	}
	return result, nil
}
