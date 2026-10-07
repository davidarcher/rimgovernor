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
	// a standing bench (#233), and the refrigeration goal whose solar-flare
	// answer is a cook-ahead bill (#408), and the art goal's pinned sculpture
	// bills (#1190), and the baby feeding goal's baby food bill (#1681), and the mech goal's gestation bills (#1686), and the surgery goal's part bills (#1168, #1755).
	need, bound := owner.ownerNeed(review)
	bound = bound && (need == policy.EnsureCooking || need == policy.EnsureFoodSupply || need == policy.MaintainFoodStorage || need == policy.MaintainResource || need == policy.MaintainAnimalFeed || need == policy.MaintainEquipment || need == policy.MaintainRefrigeration || need == policy.MaintainArt || need == policy.MaintainBabyFeeding || need == policy.MaintainMechs || need == policy.MaintainSurgery)
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

// BillPending reports whether an unretired plan still carries an unfinished
// bill for the bench and recipe. A claim is only recorded once a write is
// receipted, so two planners of one step could otherwise both pick the same
// bench from the same before-token, and the second dispatch would hold on
// the stale token forever (#408, the cook-ahead bill beside EnsureCooking's).
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
