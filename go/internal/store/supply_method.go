package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// SupplyClaims names every item this world's routine Allow completed. The
// cohort itself never adopts later forbids, but an item allowed and then
// re-forbidden by the player before the next census still reads forbidden,
// and a completed Allow must not be repeated. A cancelled or unsuccessful
// attempt (the stack left its cell before the write) claims nothing, so the
// next census can re-target the same stack where it now lies.
//
// Only a plan holding a supply action with an observe transition can carry
// a completed Allow (Completed is reached solely through Observe), so the
// link query excludes the rest before the full plan load.
func (s *Store) SupplyClaims(ctx context.Context, world World) (map[string]bool, error) {
	tx, err := s.begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()
	out, err := supplyClaims(ctx, tx, world)
	if err != nil {
		return nil, err
	}
	return out, tx.Commit()
}
func supplyClaims(ctx context.Context, tx *sql.Tx, world World) (map[string]bool, error) {
	rows, err := tx.QueryContext(ctx, `SELECT DISTINCT a.plan_id FROM actions a JOIN transitions t ON t.action_id=a.id
 WHERE a.kind=? AND json_extract(t.payload,'$.Kind')='observe' ORDER BY a.plan_id LIMIT 257`, domain.SupplyAllowAction)
	if err != nil {
		return nil, err
	}
	var plans []domain.PlanID
	for rows.Next() {
		var id domain.PlanID
		if err = rows.Scan(&id); err != nil {
			rows.Close()
			return nil, err
		}
		plans = append(plans, id)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return nil, err
	}
	if len(plans) > 256 {
		return nil, ErrCapacity
	}
	out := map[string]bool{}
	for _, id := range plans {
		plan, err := load(ctx, tx, id)
		if err != nil {
			return nil, err
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			supply, ok := progress.Action().SupplyAllow()
			effect, known := v.Effect.Value()
			renewed, renewedKnown := v.UnsuccessfulReason.Value()
			claimed := known && effect == domain.EffectCompleted && v.Stage == domain.Completed || renewedKnown && renewed == domain.OutcomeNotAchieved && v.Stage == domain.Unsuccessful
			if !ok || !claimed || v.Snapshot.Colony != world.Colony || v.Snapshot.Load != world.Load || v.Snapshot.Map != world.Map {
				continue
			}
			out[supply.Thing()] = true
		}
	}
	return out, nil
}

// admitSupplyMethod admits only stacks the review's cohort still lists, each
// at the cell the census last reported, in bounded batches.
func admitSupplyMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasSupply := false
	for _, action := range plan.Actions() {
		hasSupply = hasSupply || action.Kind() == domain.SupplyAllowAction || action.Kind() == domain.SupplyForbidAction
	}
	if !hasSupply {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > 8 {
		return ErrConflict
	}
	bound := false
	reserve := map[string]ReserveSupply{}
	var cohort []policy.StartingSupply
	for _, binding := range review.Goals {
		if binding.Goal == goal.Goal.ID {
			switch binding.Need {
			case policy.AllowStartingSupplies:
				bound = true
				cohort = review.StartingSupplies.Pending
			case policy.ManageSupplySafety:
				bound = true
				cohort = review.EventLoot.Pending
			case policy.MaintainFoodStorage:
				bound = true
				cohort = review.LarderSupplies
				for _, row := range review.ReserveSupplies {
					reserve[row.Thing] = row
				}
			}
		}
	}
	if !bound {
		return ErrConflict
	}
	pending := map[string]policy.StartingSupply{}
	for _, row := range cohort {
		pending[row.Thing] = row
	}
	for _, action := range plan.Actions() {
		supply, ok := action.SupplyAllow()
		if !ok {
			return ErrConflict
		}
		row, listed := pending[supply.Thing()]
		if entry, selected := reserve[supply.Thing()]; selected && entry.Definition == supply.Definition() && entry.Forbid == supply.Forbidden() {
			continue
		}
		if !listed || row.Definition != supply.Definition() || row.Cell != supply.Cell() || row.Forbid != supply.Forbidden() {
			return ErrConflict
		}
	}
	return nil
}

// A standing refill bill must not delay access to food in an emergency.
func reserveAccessOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) (bool, error) {
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, binding := range review.Goals {
		bound = bound || binding.Goal == goal.Goal.ID && binding.Need == policy.MaintainFoodStorage
	}
	if !bound || len(review.ReserveSupplies) == 0 || len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, action := range plan.Actions() {
		supply, ok := action.SupplyAllow()
		if !ok {
			return false, nil
		}
		selected := false
		for _, row := range review.ReserveSupplies {
			selected = selected || row.Thing == supply.Thing() && row.Definition == supply.Definition() && row.Forbid == supply.Forbidden()
		}
		if !selected {
			return false, nil
		}
	}
	for _, method := range goal.Methods {
		existing, err := load(ctx, tx, method.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range existing.Progress {
			if progress.Action().Kind() != domain.ProductionBillAction && domain.GoalWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}
