package store

import (
	"context"
	"database/sql"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func admitBillMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	has := false
	for _, a := range plan.Actions() {
		has = has || a.Kind() == domain.ProductionBillAction
	}
	if !has {
		return nil
	}
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() || len(plan.Actions()) > 4 {
		return fmt.Errorf("%w: bill method needs a current autopilot review and at most four actions", ErrConflict)
	}
	// Bills serve the cooking/food goals, the resource-target goals whose
	// production path (RoundsResourcePlanner.dispatchResourceConcern) stages a
	// bench and then a StockTarget bill on it, the equipment goal whose
	// replacement (RoundsGearPlanner, GearProduce) is a StockTarget bill on
	// a standing bench, and the refrigeration goal whose solar-flare
	// answer is a cook-ahead bill, and the art goal's pinned sculpture
	// bills, and the baby feeding goal's baby food bill, and the mech goal's gestation bills, and the surgery goal's part bills.
	need, bound := owner.ownerNeed(review)
	bound = bound && (need == policy.EnsureCooking || need == policy.EnsureFoodSupply || need == policy.MaintainFoodStorage || need == policy.MaintainResource || need == policy.MaintainEquipment || need == policy.MaintainRefrigeration || need == policy.MaintainArt || need == policy.MaintainBabyFeeding || need == policy.MaintainMechs || need == policy.MaintainSurgery)
	if !bound {
		return fmt.Errorf("%w: %s does not admit production bills", ErrConflict, owner.ownerLabel())
	}
	benches := map[string]bool{}
	for _, a := range plan.Actions() {
		b, ok := a.ProductionBill()
		if !ok || benches[b.Bench()] {
			return fmt.Errorf("%w: bill method mixes action kinds or repeats a bench", ErrConflict)
		}
		benches[b.Bench()] = true
		var n int
		if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM bill_claims WHERE colony=? AND load_token=? AND map_id=? AND bench=? AND recipe=?", owner.ownerSnapshot().Colony, owner.ownerSnapshot().Load, owner.ownerSnapshot().Map, b.Bench(), b.ClaimRecipe()).Scan(&n); err != nil {
			return err
		}
		// Finite batches expire. Fresh stack CAS and active-bill census guard
		// them; a historical standing-bill claim must not prohibit renewal.
		if n != 0 && b.Replaces() == "" && b.Mode() != domain.GearBatch {
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

// BillPending reports an unfinished bill on an unretired plan for the bench and recipe.
// Claims are recorded after receipts, so pending plans must also prevent two planners from
// selecting the same bench token.
func (s *Store) BillPending(ctx context.Context, bench, recipe string) (bool, error) {
	if submissionID(bench) != nil || submissionID(recipe) != nil {
		return false, ErrConflict
	}
	plans, err := s.LoadPlans(ctx)
	if err != nil {
		return false, err
	}
	for _, plan := range plans {
		for i, a := range plan.Spec.Actions() {
			b, ok := a.ProductionBill()
			if !ok || b.Bench() != bench || b.Recipe() != recipe || i >= len(plan.Progress) {
				continue
			}
			switch plan.Progress[i].View().Stage {
			case domain.Completed, domain.Cancelled, domain.Unsuccessful:
			default:
				return true, nil
			}
		}
	}
	return false, nil
}
