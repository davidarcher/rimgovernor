package store

import (
	"context"
	"database/sql"
	"errors"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Only the journal may retain an issued upkeep obligation. Review callers cannot
// invent a pending order, and retired observed methods need no fresh hold.
func routineUpkeepIssued(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot) (map[policy.GoalID]bool, error) {
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	result := map[policy.GoalID]bool{}
	for _, plan := range plans {
		issued := false
		for _, p := range plan.Progress {
			issued = issued || p.View().Attempt != 0 && domain.GoalWorkOpen([]domain.Progress{p})
		}
		if !issued {
			continue
		}
		var goal domain.GoalID
		err := tx.QueryRowContext(ctx, "SELECT goal_id FROM goal_methods WHERE plan_id=?", plan.Spec.ID()).Scan(&goal)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return nil, err
		}
		g, err := loadGoal(ctx, tx, goal)
		if err != nil {
			return nil, err
		}
		s := g.Goal.Snapshot
		if s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map || g.Goal.Source != domain.AutopilotGoal || !strings.HasPrefix(string(goal), "routine-") {
			continue
		}
		// Invalidated methods keep their original goal binding while new routine
		// goals replace the current review. Their unresolved effects still count.
		for _, need := range []policy.GoalID{policy.MaintainFireSafety, policy.SecureSupplies, policy.MaintainEssentialRepairs, policy.MaintainCleanFacilities} {
			if strings.HasSuffix(string(goal), "-"+string(need)) {
				result[need] = true
			}
		}
	}
	return result, nil
}
