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
			if !ok || !known || effect != domain.EffectCompleted || v.Stage != domain.Completed || v.Snapshot.Colony != world.Colony || v.Snapshot.Load != world.Load || v.Snapshot.Map != world.Map {
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
		hasSupply = hasSupply || action.Kind() == domain.SupplyAllowAction
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
	for _, binding := range review.Goals {
		bound = bound || binding.Need == policy.AllowStartingSupplies && binding.Goal == goal.Goal.ID
	}
	if !bound {
		return ErrConflict
	}
	pending := map[string]policy.StartingSupply{}
	for _, row := range review.StartingSupplies.Pending {
		pending[row.Thing] = row
	}
	for _, action := range plan.Actions() {
		supply, ok := action.SupplyAllow()
		if !ok {
			return ErrConflict
		}
		row, listed := pending[supply.Thing()]
		if !listed || row.Definition != supply.Definition() || row.Cell != supply.Cell() {
			return ErrConflict
		}
	}
	return nil
}
