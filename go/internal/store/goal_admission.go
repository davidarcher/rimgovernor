package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type BuildingMethodRequest struct {
	Goal     domain.GoalID
	Revision uint64
	Method   domain.MethodID
	Plan     domain.PlanSpec
	Current  domain.GenerationSnapshot
	Tick     domain.Tick
	Bounds   domain.Fact[policy.Bounds]
	Stock    policy.StockObservation
	Rules    []policy.ResourceRule
	Previews []policy.Preview
	Purpose  policy.Purpose
}

type BuildingMethodDecision struct {
	Admitted bool
	Goal     GoalState
	Refused  []policy.Refusal
}

// AdmitBuildingMethod applies the same pure resource/geometry policy as Hands
// inside the transaction that stores every method reservation and its plan.
// Candidates can depend on future work in this method: admission reserves the
// whole project, while the persisted dependency graph gates preparation/dispatch.
func (s *Store) AdmitBuildingMethod(ctx context.Context, r BuildingMethodRequest) (BuildingMethodDecision, error) {
	if err := r.Plan.Validate(); err != nil {
		return BuildingMethodDecision{}, err
	}
	if r.Current.Validate() != nil || r.Current.Plan != r.Plan.ID() || r.Current.Revision != r.Plan.Revision() || r.Tick < 0 {
		return BuildingMethodDecision{}, errors.New("invalid method admission scope")
	}
	actions := r.Plan.Actions()
	if len(actions) == 0 || len(actions) != len(r.Previews) || len(actions) > 256 {
		return BuildingMethodDecision{}, errors.New("complete bounded method previews required")
	}
	previews := map[domain.ActionID]policy.Preview{}
	for _, p := range r.Previews {
		if _, exists := previews[p.Action.ID()]; exists {
			return BuildingMethodDecision{}, errors.New("duplicate method preview")
		}
		previews[p.Action.ID()] = p
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return BuildingMethodDecision{}, err
	}
	defer tx.Rollback()
	goal, err := loadGoal(ctx, tx, r.Goal)
	if err != nil {
		return BuildingMethodDecision{}, err
	}
	if goal.Revision != r.Revision {
		return BuildingMethodDecision{}, ErrConflict
	}
	g := goal.Goal
	old := g.Snapshot
	if old.Colony != r.Current.Colony || old.Load != r.Current.Load || old.Map != r.Current.Map || old.Direction != r.Current.Direction || r.Tick < g.Tick {
		return BuildingMethodDecision{}, errors.New("method observation differs from reviewed goal")
	}
	var candidates []policy.Candidate
	for _, a := range actions {
		if a.Kind() != domain.BuildingAction {
			return BuildingMethodDecision{}, errors.New("unsupported method action family")
		}
		preview, exists := previews[a.ID()]
		if !exists || preview.Action != a {
			return BuildingMethodDecision{}, errors.New("method preview does not match action")
		}
		progress, err := domain.NewProgress(r.Plan, a.ID())
		if err != nil {
			return BuildingMethodDecision{}, err
		}
		candidates = append(candidates, policy.Candidate{Action: a, Progress: progress, Priority: int32(4 - g.Priority), Purpose: r.Purpose, Preview: preview})
	}
	held, err := buildingMethodHolds(ctx, tx, r.Current)
	if err != nil {
		return BuildingMethodDecision{}, err
	}
	input, err := policy.NewInput(policy.Request{Current: r.Current, CurrentTick: r.Tick, Bounds: r.Bounds, Candidates: candidates, Stock: r.Stock, Held: held, Rules: r.Rules})
	if err != nil {
		return BuildingMethodDecision{}, err
	}
	decision := policy.Admit(input)
	if len(decision.Refused) != 0 || len(decision.Admitted) != len(actions) {
		return BuildingMethodDecision{Goal: goal, Refused: decision.Refused}, nil
	}
	goal, err = commitGoalMethod(ctx, tx, r.Goal, r.Revision, r.Method, r.Plan)
	if err != nil {
		return BuildingMethodDecision{}, err
	}
	for _, h := range decision.Admitted {
		costs := make([]MaterialCost, len(h.Costs))
		for i, c := range h.Costs {
			costs[i] = MaterialCost{Definition: string(c.Resource), Count: c.Count}
		}
		admission := Admission{Snapshot: h.Snapshot, Tick: r.Tick, Costs: costs, Footprint: h.Footprint}
		if err = validateAdmission(h.Action, h.Progress, admission); err != nil {
			return BuildingMethodDecision{}, err
		}
		data, err := json.Marshal(admission)
		if err != nil {
			return BuildingMethodDecision{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO admissions(action_id,payload) VALUES(?,?)", h.Action.ID(), data); err != nil {
			return BuildingMethodDecision{}, err
		}
	}
	if err = tx.Commit(); err != nil {
		return BuildingMethodDecision{}, err
	}
	return BuildingMethodDecision{Admitted: true, Goal: goal}, nil
}

// Read authoritative reservations under the admission transaction. Never accept
// a caller's potentially stale inventory of competing projects.
func buildingMethodHolds(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot) ([]policy.Reservation, error) {
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	var held []policy.Reservation
	for _, p := range plans {
		records := map[domain.ActionID]Admission{}
		for _, a := range p.Admissions {
			records[a.Action] = a.Admission
		}
		for _, progress := range p.Progress {
			if progress.Action().Kind() != domain.BuildingAction {
				continue
			}
			v := progress.View()
			effect, k := v.Effect.Value()
			if v.Stage == domain.Cancelled && !v.Unresolved && (v.Attempt == 0 || k && effect == domain.EffectAbsent) {
				continue
			}
			admission, exists := records[v.Action]
			if !exists {
				if v.Stage == domain.Pending && v.Attempt == 0 && !v.Unresolved {
					continue
				}
				return nil, errors.New("existing building work lacks accounting evidence")
			}
			scope := admission.Snapshot
			if scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map {
				continue
			}
			costs := make([]policy.Amount, len(admission.Costs))
			for i, c := range admission.Costs {
				costs[i] = policy.Amount{Resource: policy.Resource(c.Definition), Count: c.Count}
			}
			held = append(held, policy.Reservation{Action: progress.Action(), Progress: progress, Snapshot: scope, Costs: costs, Footprint: admission.Footprint})
		}
	}
	return held, nil
}
