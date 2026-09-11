package executor

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func persistentHolds(state store.PlanState, target domain.ActionID, expected domain.Progress) ([]policy.Reservation, error) {
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
		if action.ID() == target {
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
		record, present := records[action.ID()]
		if !present {
			if v.Stage == domain.Pending && v.Attempt == 0 && !v.Unresolved {
				continue
			}
			return nil, fmt.Errorf("%w: missing durable admission for %s", ErrHeld, action.ID())
		}
		// The sole candidate replaces its own safely unissued hold only after
		// successful admission. SQLite retains the old record on any failure.
		if action.ID() == target && noEffect && (v.Stage == domain.Pending || v.Stage == domain.Prepared) {
			continue
		}
		costs := make([]policy.Amount, len(record.Costs))
		for i, cost := range record.Costs {
			costs[i] = policy.Amount{Resource: policy.Resource(cost.Definition), Count: cost.Count}
		}
		held = append(held, policy.Reservation{Action: action, Progress: p, Snapshot: record.Snapshot, Costs: costs, Footprint: append([]domain.Cell(nil), record.Footprint...)})
	}
	if !found {
		return nil, ErrEvidence
	}
	return held, nil
}
