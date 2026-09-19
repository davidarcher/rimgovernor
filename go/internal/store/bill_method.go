package store

import (
	"context"
	"database/sql"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitBillMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	has := false
	for _, a := range plan.Actions() {
		has = has || a.Kind() == domain.ProductionBillAction
	}
	if !has {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > 4 {
		return fmt.Errorf("%w: bill method needs a current autopilot review and at most four actions", ErrConflict)
	}
	// Bills serve the cooking/food goals, the resource-target goals whose
	// production path (RoutineResourcePlanner.dispatchResourceGoal) stages a
	// bench and then a StockTarget bill on it, and the equipment goal whose
	// replacement (RoutineGearPlanner, GearProduce) is a StockTarget bill on
	// a standing bench (#233).
	bound := false
	for _, b := range review.Goals {
		bound = bound || b.Goal == goal.Goal.ID && (b.Need == policy.EnsureCooking || b.Need == policy.EnsureFoodSupply || b.Need == policy.MaintainResource || b.Need == policy.MaintainAnimalFeed || b.Need == policy.MaintainEquipment)
	}
	if !bound {
		return fmt.Errorf("%w: goal %s does not admit production bills", ErrConflict, goal.Goal.ID)
	}
	benches := map[string]bool{}
	for _, a := range plan.Actions() {
		b, ok := a.ProductionBill()
		if !ok || benches[b.Bench()] {
			return fmt.Errorf("%w: bill method mixes action kinds or repeats a bench", ErrConflict)
		}
		benches[b.Bench()] = true
		var n int
		if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM bill_claims WHERE colony=? AND load_token=? AND map_id=? AND bench=? AND recipe=?", goal.Goal.Snapshot.Colony, goal.Goal.Snapshot.Load, goal.Goal.Snapshot.Map, b.Bench(), b.Recipe()).Scan(&n); err != nil {
			return err
		}
		if n != 0 {
			return fmt.Errorf("%w: bench %s already has a claimed %s bill", ErrConflict, b.Bench(), b.Recipe())
		}
	}
	return nil
}

func (s *Store) BillClaimed(ctx context.Context, current domain.GenerationSnapshot, bench, recipe string) (bool, error) {
	if current.Validate() != nil || submissionID(bench) != nil || submissionID(recipe) != nil {
		return false, ErrConflict
	}
	var count int
	err := s.db.QueryRowContext(ctx, "SELECT count(*) FROM bill_claims WHERE colony=? AND load_token=? AND map_id=? AND bench=? AND recipe=?", current.Colony, current.Load, current.Map, bench, recipe).Scan(&count)
	return count != 0, err
}
