package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// SupplyClaims retains admitted item identities across player directions. A
// cancelled or uncertain batch cannot adopt a later player forbid as new work.
func (s *Store) SupplyClaims(ctx context.Context, world World) (map[string]bool, error) {
	rows, err := s.db.QueryContext(ctx, "SELECT thing FROM supply_claims WHERE colony=? AND load_token=? AND map_id=?", world.Colony, world.Load, world.Map)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]bool{}
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			return nil, err
		}
		out[id] = true
	}
	return out, rows.Err()
}

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
	cells := map[domain.Cell]bool{}
	for _, cell := range review.StartingSupplies.Pending {
		cells[cell] = true
	}
	for _, action := range plan.Actions() {
		supply, ok := action.SupplyAllow()
		if !ok || !cells[supply.Cell()] {
			return ErrConflict
		}
		scope := review.Snapshot
		if _, err = tx.ExecContext(ctx, "INSERT INTO supply_claims(colony,load_token,map_id,thing) VALUES(?,?,?,?)", scope.Colony, scope.Load, scope.Map, supply.Thing()); err != nil {
			return conflict(err)
		}
	}
	return nil
}
