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
func roundsUpkeepIssued(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot) (map[policy.ConcernID]bool, error) {
	plans, err := loadPlans(ctx, tx)
	if err != nil {
		return nil, err
	}
	result := map[policy.ConcernID]bool{}
	for _, plan := range plans {
		issued := false
		for _, p := range plan.Progress {
			issued = issued || p.View().Attempt != 0 && domain.StandardWorkOpen([]domain.Progress{p})
		}
		if !issued {
			continue
		}
		var goal domain.ConcernID
		err := tx.QueryRowContext(ctx, "SELECT owner_id FROM plan_methods WHERE plan_id=? AND kind='standard'", plan.Spec.ID()).Scan(&goal)
		if errors.Is(err, sql.ErrNoRows) {
			continue
		}
		if err != nil {
			return nil, err
		}
		g, err := loadStandard(ctx, tx, goal)
		if err != nil {
			return nil, err
		}
		s := g.Standard.Snapshot
		if s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map || !strings.HasPrefix(string(goal), "routine-") {
			continue
		}
		// Invalidated methods keep their original goal binding while new routine
		// goals replace the current review. Their unresolved effects still count.
		for _, need := range []policy.ConcernID{policy.ClearHomeObstructions, policy.MaintainFireSafety, policy.MaintainEssentialRepairs, policy.MaintainCleanFacilities, policy.MaintainMedicalReserves, policy.MaintainFoodStorage, policy.MaintainAnimalContainment, policy.MaintainHousing, policy.MaintainHomeCoverage, policy.MaintainStoneShell} {
			if roundsStandardOwns(goal, need) {
				result[need] = true
			}
		}
	}
	return result, nil
}
