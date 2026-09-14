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

// BedAssignSubmissionRequest is explicit player intent to assign one
// already-observed undrafted pawn to one already-observed bed. It mirrors
// TendSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type BedAssignSubmissionRequest struct {
	RequestID string
	World     World
	Assign    domain.BedAssign
}
type BedAssignSubmission struct {
	Request  BedAssignSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}
type bedAssignPayload struct {
	Pawn          domain.PawnID
	Bed           string
	PreviousClear bool
	PreviousID    string
}

func (q BedAssignSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewBedAssign(q.Assign.Pawn(), q.Assign.Bed(), q.Assign.PreviousBed())
	return err
}

// SubmitBedAssign atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitTend uses.
func (s *Store) SubmitBedAssign(ctx context.Context, q BedAssignSubmissionRequest) (BedAssignSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return BedAssignSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BedAssignSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupBedAssignSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return BedAssignSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return BedAssignSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return BedAssignSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return BedAssignSubmission{}, false, err
	}
	result := BedAssignSubmission{Request: q, Plan: domain.PlanID("bed-assign-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("bed-assign-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewBedAssignAction(result.Action, q.Assign)
	if err != nil {
		return BedAssignSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return BedAssignSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return BedAssignSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "bed_assign", q.World, result.Plan, result.Action); err != nil {
		return BedAssignSubmission{}, false, err
	}
	data, err := json.Marshal(bedAssignPayload{q.Assign.Pawn(), q.Assign.Bed(), q.Assign.PreviousBed().Clear(), q.Assign.PreviousBed().ID()})
	if err != nil {
		return BedAssignSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO bed_assign_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return BedAssignSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return BedAssignSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupBedAssignSubmission(ctx context.Context, requestID string) (BedAssignSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return BedAssignSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BedAssignSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupBedAssignSubmission(ctx, tx, requestID)
	if err != nil {
		return BedAssignSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return BedAssignSubmission{}, err
	}
	return result, nil
}
func lookupBedAssignSubmission(ctx context.Context, tx *sql.Tx, id string) (BedAssignSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "bed_assign")
	if err != nil {
		return BedAssignSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM bed_assign_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return BedAssignSubmission{}, ErrNotFound
	}
	if err != nil {
		return BedAssignSubmission{}, err
	}
	if len(data) > 32768 {
		return BedAssignSubmission{}, errors.New("bed assign submission exceeds bound")
	}
	var payload bedAssignPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return BedAssignSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return BedAssignSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return BedAssignSubmission{}, errors.New("noncanonical bed assign submission")
	}
	var previous domain.PreviousBed
	if payload.PreviousClear {
		previous = domain.ClearPreviousBed()
	} else {
		previous, err = domain.KnownPreviousBed(payload.PreviousID)
		if err != nil {
			return BedAssignSubmission{}, err
		}
	}
	assign, err := domain.NewBedAssign(payload.Pawn, payload.Bed, previous)
	if err != nil {
		return BedAssignSubmission{}, err
	}
	result := BedAssignSubmission{Request: BedAssignSubmissionRequest{RequestID: id, World: h.World, Assign: assign}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return BedAssignSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return BedAssignSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return BedAssignSubmission{}, errors.New("bed assign submission plan is corrupt")
	}
	actual, ok := actions[0].BedAssign()
	if !ok || actual != assign {
		return BedAssignSubmission{}, errors.New("bed assign submission differs from intent")
	}
	return result, nil
}
