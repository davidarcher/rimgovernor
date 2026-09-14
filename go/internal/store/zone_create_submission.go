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

// ZoneCreateSubmissionRequest is explicit player intent to create one
// closed-preset zone over an already-observed connected footprint. It
// mirrors QuestAcceptSubmissionRequest: this family is player-command-
// driven, not a fixed-priority routine producer (unlike the autopilot-bound
// admission zone/admitZoneMethod gates), so a submitted request commits its
// own one-action plan immediately instead of being composed by a routine
// planner or bound to an autopilot goal.
type ZoneCreateSubmissionRequest struct {
	RequestID string
	World     World
	Zone      domain.ZoneCreate
}
type ZoneCreateSubmission struct {
	Request  ZoneCreateSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

type zoneCreatePayload struct {
	Kind     domain.ZoneKind
	Crop     string
	Preset   domain.StockpilePreset
	Priority domain.StockpilePriority
	Cells    []domain.Cell
	Allow    []string
}

// reconstructZoneCreate rebuilds a canonical ZoneCreate from its exported
// fields by dispatching on kind/preset to the family-specific constructor,
// mirroring domain.ReconstructZone's own dispatch.
func reconstructZoneCreate(p zoneCreatePayload) (domain.ZoneCreate, error) {
	switch p.Kind {
	case domain.GrowingZone:
		return domain.NewZoneCreate(p.Kind, p.Crop, p.Cells)
	case domain.StockpileZone:
		switch p.Preset {
		case domain.FoodPreset:
			return domain.NewStockpileZone(p.Preset, p.Priority, p.Cells)
		case domain.NothingPreset:
			return domain.NewAllowListStockpileZone(p.Priority, p.Allow, p.Cells)
		}
	}
	return domain.ZoneCreate{}, errors.New("unsupported zone configuration")
}
func zoneCreatePayloadOf(z domain.ZoneCreate) zoneCreatePayload {
	return zoneCreatePayload{z.Kind(), z.Crop(), z.Preset(), z.Priority(), z.Cells(), z.Allow()}
}

func (z ZoneCreateSubmissionRequest) validate() error {
	if err := submissionID(z.RequestID); err != nil {
		return err
	}
	if err := z.World.Validate(); err != nil {
		return err
	}
	canonical, err := reconstructZoneCreate(zoneCreatePayloadOf(z.Zone))
	if err != nil || canonical != z.Zone {
		return errors.New("invalid zone configuration")
	}
	return nil
}

// SubmitZoneCreate atomically stores one explicit player intent and its
// one-action plan, the same shape SubmitQuestAccept uses. Submission neither
// acquires authority nor issues the native CreateZone command; a worker
// later admits and dispatches the committed action.
func (s *Store) SubmitZoneCreate(ctx context.Context, z ZoneCreateSubmissionRequest) (ZoneCreateSubmission, bool, error) {
	if err := z.validate(); err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupZoneCreateSubmission(ctx, tx, z.RequestID)
	if err == nil {
		if old.Request != z {
			return ZoneCreateSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return ZoneCreateSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return ZoneCreateSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	result := ZoneCreateSubmission{Request: z, Plan: domain.PlanID("zone-create-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("zone-create-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewZoneCreateAction(result.Action, z.Zone)
	if err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, z.RequestID, "zone_create", z.World, result.Plan, result.Action); err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	data, err := json.Marshal(zoneCreatePayloadOf(z.Zone))
	if err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO zone_create_submissions(request_id,payload) VALUES(?,?)", z.RequestID, data); err != nil {
		return ZoneCreateSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return ZoneCreateSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupZoneCreateSubmission(ctx context.Context, requestID string) (ZoneCreateSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return ZoneCreateSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupZoneCreateSubmission(ctx, tx, requestID)
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return ZoneCreateSubmission{}, err
	}
	return result, nil
}
func lookupZoneCreateSubmission(ctx context.Context, tx *sql.Tx, id string) (ZoneCreateSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "zone_create")
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM zone_create_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return ZoneCreateSubmission{}, ErrNotFound
	}
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	if len(data) > 32768 {
		return ZoneCreateSubmission{}, errors.New("zone create submission exceeds bound")
	}
	var payload zoneCreatePayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return ZoneCreateSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return ZoneCreateSubmission{}, errors.New("noncanonical zone create submission")
	}
	zone, err := reconstructZoneCreate(payload)
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	result := ZoneCreateSubmission{Request: ZoneCreateSubmissionRequest{RequestID: id, World: h.World, Zone: zone}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return ZoneCreateSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return ZoneCreateSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return ZoneCreateSubmission{}, errors.New("zone create submission plan is corrupt")
	}
	actual, ok := actions[0].ZoneCreate()
	if !ok || actual != zone {
		return ZoneCreateSubmission{}, errors.New("zone create submission differs from intent")
	}
	return result, nil
}
