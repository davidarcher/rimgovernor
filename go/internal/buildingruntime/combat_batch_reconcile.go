package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (r *RoundsDefensePlanner) reconcileCombatBatches(ctx context.Context, state ControlState, incident store.IncidentState, view policy.CombatView) error {
	for _, method := range incident.Methods {
		plan, err := r.reviewer.player.journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		if plan.Retired || len(plan.Progress) != 1 {
			continue
		}
		p := plan.Progress[0]
		batch, ok := p.Action().CombatBatch()
		if !ok || !p.View().Unresolved || view.Tick <= p.View().Tick || !policy.CombatBatchObserved(batch, view) {
			continue
		}
		current := state.Snapshot
		current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
		observation := domain.Observation{Action: p.Action().ID(), Attempt: p.View().Attempt, Snapshot: current, Tick: view.Tick, Effect: domain.EffectCompleted}
		if _, err = r.reviewer.player.journal.Observe(ctx, plan.Spec.ID(), observation, current); err != nil {
			return err
		}
		if err = r.reviewer.player.journal.RetireCombatBatch(ctx, plan.Spec.ID()); err != nil {
			return err
		}
	}
	return nil
}
