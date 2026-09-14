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

// BuildingTemperatureSubmissionRequest is explicit player intent to patch
// one already-observed temperature-controlled building's target setpoint,
// CAS-gated by an already-observed exact snapshot token. It mirrors
// TendSubmissionRequest: this family is player-command-driven, not a
// fixed-priority routine producer, so a submitted request commits its own
// one-action plan immediately instead of being composed by a routine
// planner.
type BuildingTemperatureSubmissionRequest struct {
	RequestID string
	World     World
	Patch     domain.BuildingTemperature
}
type BuildingTemperatureSubmission struct {
	Request  BuildingTemperatureSubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}
type buildingTemperatureSubmissionPayload struct {
	Thing   string
	Celsius float64
	Before  string
}

func (q BuildingTemperatureSubmissionRequest) validate() error {
	if err := submissionID(q.RequestID); err != nil {
		return err
	}
	if err := q.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewBuildingTemperature(q.Patch.Thing(), q.Patch.Celsius(), q.Patch.BeforeToken())
	return err
}

// SubmitBuildingTemperature atomically stores one explicit player intent and
// its one-action plan, the same shape SubmitTend uses.
func (s *Store) SubmitBuildingTemperature(ctx context.Context, q BuildingTemperatureSubmissionRequest) (BuildingTemperatureSubmission, bool, error) {
	if err := q.validate(); err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	defer tx.Rollback()
	old, err := lookupBuildingTemperatureSubmission(ctx, tx, q.RequestID)
	if err == nil {
		if old.Request != q {
			return BuildingTemperatureSubmission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return BuildingTemperatureSubmission{}, false, err
		}
		return old, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return BuildingTemperatureSubmission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	result := BuildingTemperatureSubmission{Request: q, Plan: domain.PlanID("building-temperature-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("building-temperature-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewBuildingTemperatureAction(result.Action, q.Patch)
	if err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	if err = insertSubmissionHeader(ctx, tx, q.RequestID, "building_temperature", q.World, result.Plan, result.Action); err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	data, err := json.Marshal(buildingTemperatureSubmissionPayload{q.Patch.Thing(), q.Patch.Celsius(), q.Patch.BeforeToken()})
	if err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	if _, err = tx.ExecContext(ctx, "INSERT INTO building_temperature_submissions(request_id,payload) VALUES(?,?)", q.RequestID, data); err != nil {
		return BuildingTemperatureSubmission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return BuildingTemperatureSubmission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupBuildingTemperatureSubmission(ctx context.Context, requestID string) (BuildingTemperatureSubmission, error) {
	if err := submissionID(requestID); err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	defer tx.Rollback()
	result, err := lookupBuildingTemperatureSubmission(ctx, tx, requestID)
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	if err = tx.Commit(); err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	return result, nil
}
func lookupBuildingTemperatureSubmission(ctx context.Context, tx *sql.Tx, id string) (BuildingTemperatureSubmission, error) {
	h, err := lookupSubmissionHeader(ctx, tx, id, "building_temperature")
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	var data []byte
	err = tx.QueryRowContext(ctx, "SELECT payload FROM building_temperature_submissions WHERE request_id=?", id).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return BuildingTemperatureSubmission{}, ErrNotFound
	}
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	if len(data) > 32768 {
		return BuildingTemperatureSubmission{}, errors.New("building temperature submission exceeds bound")
	}
	var payload buildingTemperatureSubmissionPayload
	if err = json.Unmarshal(data, &payload); err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	canonical, err := json.Marshal(payload)
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	if !bytes.Equal(canonical, data) {
		return BuildingTemperatureSubmission{}, errors.New("noncanonical building temperature submission")
	}
	patch, err := domain.NewBuildingTemperature(payload.Thing, payload.Celsius, payload.Before)
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	result := BuildingTemperatureSubmission{Request: BuildingTemperatureSubmissionRequest{RequestID: id, World: h.World, Patch: patch}, Revision: h.Revision, Plan: h.Plan, Action: h.Action}
	if err = result.Request.validate(); err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return BuildingTemperatureSubmission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return BuildingTemperatureSubmission{}, errors.New("building temperature submission plan is corrupt")
	}
	actual, ok := actions[0].BuildingTemperature()
	if !ok || actual != patch {
		return BuildingTemperatureSubmission{}, errors.New("building temperature submission differs from intent")
	}
	return result, nil
}
