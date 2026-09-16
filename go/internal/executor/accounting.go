package executor

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func persistentHolds(state store.PlanState, target domain.ActionID, expected domain.Progress) ([]policy.Reservation, error) {
	return planHolds(state, target, &expected)
}

// PlanCatalog must return a complete atomic active catalog or an error at the limit.
// Store.LoadPlans supplies this guarantee; partial pagination cannot establish
// that another plan has no commitments. Retired completions retain store-level
// observation floors checked again at preparation and dispatch.
type PlanCatalog interface {
	LoadPlans(context.Context, int) ([]store.PlanState, error)
}

// ExternalHolds recovers other plans' durable commitments in the current world.
// The caller must hold the runtime writer lock throughout inspection/dispatch.
// Current-plan accounting stays inside Executor, where progress is rechecked.
func ExternalHolds(ctx context.Context, catalog PlanCatalog, current domain.GenerationSnapshot) ([]policy.Reservation, error) {
	if catalog == nil {
		return nil, ErrEvidence
	}
	if err := current.Validate(); err != nil {
		return nil, err
	}
	states, err := catalog.LoadPlans(ctx, 256)
	if err != nil {
		return nil, err
	}
	if len(states) > 256 {
		return nil, ErrEvidence
	}
	result := []policy.Reservation{}
	seen := map[domain.PlanID]bool{}
	for _, state := range states {
		if seen[state.Spec.ID()] {
			return nil, ErrEvidence
		}
		seen[state.Spec.ID()] = true
		if state.Spec.ID() == current.Plan {
			continue
		}
		held, err := planHolds(state, "", nil)
		if err != nil {
			return nil, err
		}
		for _, hold := range held {
			if hold.Snapshot.Colony == current.Colony && hold.Snapshot.Map == current.Map && hold.Snapshot.Load == current.Load {
				result = append(result, hold)
			}
		}
	}
	return result, nil
}

func planHolds(state store.PlanState, target domain.ActionID, expected *domain.Progress) ([]policy.Reservation, error) {
	records := make(map[domain.ActionID]store.Admission, len(state.Admissions))
	for _, record := range state.Admissions {
		if _, duplicate := records[record.Action]; duplicate {
			return nil, ErrEvidence
		}
		records[record.Action] = record.Admission
	}
	progress := make(map[domain.ActionID]domain.Progress, len(state.Progress))
	for _, p := range state.Progress {
		if _, duplicate := progress[p.View().Action]; duplicate {
			return nil, ErrEvidence
		}
		progress[p.View().Action] = p
	}
	held := []policy.Reservation{}
	found := false
	for _, action := range state.Spec.Actions() {
		p, ok := progress[action.ID()]
		if !ok {
			return nil, ErrEvidence
		}
		v := p.View()
		if expected != nil && action.ID() == target {
			found = true
			if v != expected.View() {
				return nil, fmt.Errorf("%w: target progress changed during inspection", ErrHeld)
			}
		}
		effect, effectKnown := v.Effect.Value()
		noEffect := !v.Unresolved && (v.Attempt == 0 || effectKnown && effect == domain.EffectAbsent)
		if v.Stage == domain.Cancelled && noEffect {
			continue
		}
		// Only building placements reserve resources and cells here; every
		// other kind carries its own admission table and holds nothing, so a
		// dispatched supply, acquisition or policy action in some other plan
		// must not hold construction hostage.
		if action.Kind() != domain.BuildingAction {
			continue
		}
		record, present := records[action.ID()]
		if !present {
			if v.Stage == domain.Pending && v.Attempt == 0 && !v.Unresolved {
				continue
			}
			return nil, fmt.Errorf("%w: missing durable admission for %s", ErrHeld, action.ID())
		}
		// The sole candidate replaces its own safely unissued hold only after
		// successful admission. SQLite retains the old record on any failure.
		if expected != nil && action.ID() == target && noEffect && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
			continue
		}
		costs := make([]policy.Amount, len(record.Costs))
		for i, cost := range record.Costs {
			costs[i] = policy.Amount{Resource: policy.Resource(cost.Definition), Count: cost.Count}
		}
		held = append(held, policy.Reservation{Action: action, Progress: p, Snapshot: record.Snapshot, Costs: costs, Footprint: append([]domain.Cell(nil), record.Footprint...)})
	}
	if expected != nil && !found {
		return nil, ErrEvidence
	}
	return held, nil
}
