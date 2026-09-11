package store

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var ErrCapacity = errors.New("plan catalog capacity reached")

// World is exact native identity. Map zero is valid; no live authority is implied.
type World struct {
	Colony domain.ColonyID
	Load   domain.LoadID
	Map    domain.MapID
}

func (w World) Validate() error {
	return (domain.GenerationSnapshot{Colony: w.Colony, Load: w.Load, Map: w.Map, Plan: "world-validation"}).Validate()
}

type SubmissionRequest struct {
	RequestID string
	World     World
	Building  domain.Building
}
type Submission struct {
	Request  SubmissionRequest
	Plan     domain.PlanID
	Action   domain.ActionID
	Revision domain.PlanRevision
}

func submissionID(id string) error { _, err := domain.NewPlan(domain.PlanID(id), 1, nil); return err }
func (request SubmissionRequest) validate() error {
	if err := submissionID(request.RequestID); err != nil {
		return err
	}
	if err := request.World.Validate(); err != nil {
		return err
	}
	_, err := domain.NewBuilding(request.Building.Definition(), request.Building.Cell(), request.Building.Rotation(), request.Building.Stuff())
	return err
}

// SubmitBuilding commits one immutable player intent and its plan together.
// Replay never creates another plan or grants authority; equality is typed semantics.
func (s *Store) SubmitBuilding(ctx context.Context, request SubmissionRequest) (Submission, bool, error) {
	if err := request.validate(); err != nil {
		return Submission{}, false, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return Submission{}, false, err
	}
	defer tx.Rollback()
	existing, err := lookupSubmission(ctx, tx, request.RequestID)
	if err == nil {
		if existing.Request != request {
			return Submission{}, false, ErrConflict
		}
		if err = tx.Commit(); err != nil {
			return Submission{}, false, err
		}
		return existing, false, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return Submission{}, false, err
	}
	var entropy [32]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return Submission{}, false, err
	}
	result := Submission{Request: request, Plan: domain.PlanID("building-" + hex.EncodeToString(entropy[:16])), Action: domain.ActionID("building-action-" + hex.EncodeToString(entropy[16:])), Revision: 1}
	action, err := domain.NewBuildingAction(result.Action, request.Building)
	if err != nil {
		return Submission{}, false, err
	}
	plan, err := domain.NewPlan(result.Plan, 1, []domain.Action{action})
	if err != nil {
		return Submission{}, false, err
	}
	if err = createPlan(ctx, tx, plan); err != nil {
		return Submission{}, false, err
	}
	b := request.Building
	if _, err = tx.ExecContext(ctx, "INSERT INTO building_submissions(request_id,colony,load_token,map_id,definition,x,z,rotation,stuff,plan_id,action_id) VALUES(?,?,?,?,?,?,?,?,?,?,?)", request.RequestID, request.World.Colony, request.World.Load, request.World.Map, b.Definition(), b.Cell().X, b.Cell().Z, b.Rotation(), b.Stuff(), result.Plan, result.Action); err != nil {
		return Submission{}, false, conflict(err)
	}
	if err = tx.Commit(); err != nil {
		return Submission{}, false, err
	}
	return result, true, nil
}
func (s *Store) LookupSubmission(ctx context.Context, requestID string) (Submission, error) {
	if err := submissionID(requestID); err != nil {
		return Submission{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return Submission{}, err
	}
	defer tx.Rollback()
	result, err := lookupSubmission(ctx, tx, requestID)
	if err != nil {
		return Submission{}, err
	}
	if err = tx.Commit(); err != nil {
		return Submission{}, err
	}
	return result, nil
}
func lookupSubmission(ctx context.Context, tx *sql.Tx, id string) (Submission, error) {
	result := Submission{Request: SubmissionRequest{RequestID: id}, Revision: 1}
	var definition, stuff string
	var cell domain.Cell
	var rotation domain.Rotation
	err := tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id,definition,x,z,rotation,stuff,plan_id,action_id FROM building_submissions WHERE request_id=?", id).Scan(&result.Request.World.Colony, &result.Request.World.Load, &result.Request.World.Map, &definition, &cell.X, &cell.Z, &rotation, &stuff, &result.Plan, &result.Action)
	if errors.Is(err, sql.ErrNoRows) {
		return Submission{}, ErrNotFound
	}
	if err != nil {
		return Submission{}, err
	}
	result.Request.Building, err = domain.NewBuilding(definition, cell, rotation, stuff)
	if err != nil {
		return Submission{}, err
	}
	if err = result.Request.validate(); err != nil {
		return Submission{}, err
	}
	state, err := load(ctx, tx, result.Plan)
	if err != nil {
		return Submission{}, err
	}
	actions := state.Spec.Actions()
	if state.Spec.Revision() != 1 || len(actions) != 1 || actions[0].ID() != result.Action {
		return Submission{}, fmt.Errorf("submission plan identity is corrupt")
	}
	building, ok := actions[0].Building()
	if !ok || building != result.Request.Building {
		return Submission{}, fmt.Errorf("submission plan differs from intent")
	}
	return result, nil
}
