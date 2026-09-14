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

// ZoneEditSubmissionRequest is explicit player intent to apply one bounded
// edit (add, remove or delete) to an already-observed existing native zone.
// It mirrors ZoneCreateSubmissionRequest: this family is player-command-
// driven, not a fixed-priority routine producer, so a submitted request
// commits its own one-action plan immediately instead of being composed by
// a routine planner.
type ZoneEditSubmissionRequest struct {
	RequestID string
	World     World
	Edit      domain.ZoneEdit
}
type ZoneEditSubmission struct {
	Request  ZoneEditSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type zoneEditPayload struct {
	ZoneID string
	Before string
	Op     domain.ZoneEditOp
	Cells  []domain.Cell
}

// reconstructZoneEdit rebuilds a canonical ZoneEdit from its exported fields
// by dispatching on op to the family-specific constructor, mirroring
// domain.ReconstructZoneEdit's own dispatch.
func reconstructZoneEditPayload(p zoneEditPayload) (domain.ZoneEdit, error) {
	switch p.Op {
	case domain.ZoneEditAdd:
		return domain.NewZoneEditAdd(p.ZoneID, p.Before, p.Cells)
	case domain.ZoneEditRemove:
		return domain.NewZoneEditRemove(p.ZoneID, p.Before, p.Cells)
	case domain.ZoneEditDelete:
		return domain.NewZoneEditDelete(p.ZoneID, p.Before)
	}
	return domain.ZoneEdit{}, errors.New("unsupported zone edit configuration")
}
func zoneEditPayloadOf(z domain.ZoneEdit) zoneEditPayload {
	return zoneEditPayload{z.ZoneID(), z.BeforeToken(), z.Op(), z.Cells()}
}

func (z ZoneEditSubmissionRequest) validate() error {
	if err := submissionID(z.RequestID); err != nil {
		return err
	}
	if err := z.World.Validate(); err != nil {
		return err
	}
	canonical, err := reconstructZoneEditPayload(zoneEditPayloadOf(z.Edit))
	if err != nil || canonical != z.Edit {
		return errors.New("invalid zone edit configuration")
	}
	return nil
}

// SubmitZoneEdit atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitZoneCreate uses. Submission neither
// acquires authority nor issues the native command; a worker later admits
// and dispatches the committed action.
func (s *Store) SubmitZoneEdit(ctx context.Context, z ZoneEditSubmissionRequest) (ZoneEditSubmission, bool, error) {
	if err := z.validate(); err != nil {
		return ZoneEditSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ZoneEditSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupZoneEditSubmission(ctx, tx, z.RequestID)
	if err == nil {
		if old.Request != z {
			return ZoneEditSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ZoneEditSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ZoneEditSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return ZoneEditSubmission{}, false, err
	}
	result := ZoneEditSubmission{Request: z, Plan: domain.PlanID("zone-edit-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("zone-edit-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewZoneEditAction(result.Action, z.Edit)
	if err != nil {
		return ZoneEditSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return ZoneEditSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return ZoneEditSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, z.RequestID, "zone_edit", z.World, result.Plan, result.Action); err != nil {
		return ZoneEditSubmission{}, false, err
	}
	data, err := json.Marshal(zoneEditPayloadOf(z.Edit))
	if err != nil {
		return ZoneEditSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO zone_edit_submissions(request_id,payload) VALUES(?,?)", z.RequestID, data); err != nil {
		return ZoneEditSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return ZoneEditSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupZoneEditSubmission(ctx context.Context, requestID string) (ZoneEditSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return ZoneEditSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupZoneEditSubmission(ctx, tx, requestID)
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return ZoneEditSubmission{}, err
	}
	return result, nil
}
func lookupZoneEditSubmission(ctx context.Context, tx *sql.Tx, id string) (ZoneEditSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "zone_edit")
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM zone_edit_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return ZoneEditSubmission{}, ErrNotFound
	}
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	if len(data) > 32768 {
		return ZoneEditSubmission{}, errors.New("zone edit submission exceeds bound")
	}
	var payload zoneEditPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return ZoneEditSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return ZoneEditSubmission{}, errors.New("noncanonical zone edit submission")
	}
	edit, err := reconstructZoneEditPayload(payload)
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	result := ZoneEditSubmission{Request: ZoneEditSubmissionRequest{RequestID: id, World: h.World, Edit: edit}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return ZoneEditSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return ZoneEditSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return ZoneEditSubmission{}, errors.New("zone edit submission plan is corrupt")
	}
	actual, ok := actions[0].ZoneEdit()
	if !ok || actual != edit {
		return ZoneEditSubmission{}, errors.New("zone edit submission differs from intent")
	}
	return result, nil
}
