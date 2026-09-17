package store

import (
	"context"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/acquisition"
)

type AcquisitionAdmission = acquisition.Admission
type ActionAcquisitionAdmission = acquisition.ActionAdmission

// PrepareAcquisition atomically records the exact item CAS evidence and prepares
// pending work. An unissued Prepared action may replace its record under exactly
// the same authority; its durable preparation tick remains unchanged. A refused
// failed receipt may prepare again, while unknown, completed and cancelled work
// can never lose its accounting record through this API.
// This record is evidence, not a lease. The executor must inspect and prepare
// again before dispatch after restart, even when Prepared was persisted.
func (s *Store) PrepareAcquisition(ctx context.Context, plan domain.PlanID, action domain.ActionID, admission AcquisitionAdmission) (domain.Progress, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return domain.Progress{}, err
	}
	defer tx.Rollback()
	if err = guardGoalWork(ctx, tx, plan, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	state, err := load(ctx, tx, plan)
	if err != nil {
		return domain.Progress{}, err
	}
	if err = state.Spec.CheckDependencies(action, state.Progress, admission.Snapshot, admission.Tick); err != nil {
		return domain.Progress{}, err
	}
	var a domain.Action
	var p domain.Progress
	found := false
	for i, candidate := range state.Spec.Actions() {
		if candidate.ID() == action {
			a = candidate
			p = state.Progress[i]
			found = true
			break
		}
	}
	if !found {
		return domain.Progress{}, ErrNotFound
	}
	if err = acquisition.ValidateAdmission(a, p, admission); err != nil {
		return domain.Progress{}, err
	}
	v := p.View()
	if v.Unresolved || admission.Tick < v.Tick {
		return domain.Progress{}, errors.New("admission cannot replace unresolved or newer progress")
	}
	// A prepared action has no write outstanding (dispatch is recorded before
	// any native write), so authority that moved since its preparation
	// re-prepares it under the current snapshot instead of stranding it.
	prepare := v.Stage == domain.Pending || v.Stage == domain.Prepared && !v.Snapshot.Matches(admission.Snapshot)
	switch v.Stage {
	case domain.Pending, domain.Prepared:
		if prepare {
			p, err = p.Prepare(admission.Snapshot, admission.Tick)
			if err != nil {
				return domain.Progress{}, err
			}
		}
	default:
		return domain.Progress{}, errors.New("admission replacement requires pending or prepared work")
	}
	// Retain the newest validated observation even when preparation predates it.
	for _, old := range state.AcquisitionAdmissions {
		if old.Action == action && admission.Tick < old.Admission.Tick {
			return domain.Progress{}, errors.New("admission observation moved backwards")
		}
	}
	if err = acquisition.Insert(ctx, tx, action, admission); err != nil {
		return domain.Progress{}, err
	}
	if prepare {
		event, err := json.Marshal(transition{Kind: "prepare", Snapshot: admission.Snapshot, Tick: admission.Tick})
		if err != nil {
			return domain.Progress{}, err
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO transitions(action_id,payload) VALUES(?,?)", action, event); err != nil {
			return domain.Progress{}, err
		}
	}
	if err = tx.Commit(); err != nil {
		return domain.Progress{}, err
	}
	return p, nil
}
