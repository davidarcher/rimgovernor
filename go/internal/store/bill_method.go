package store

import (
	"context"
	"database/sql"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// MaxBillPlanActions bounds one batched bill plan to a Round's reconcile.
const MaxBillPlanActions = 64

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
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() || len(plan.Actions()) > MaxBillPlanActions {
		return fmt.Errorf("%w: bill method needs a current autopilot review and at most %d actions", ErrConflict, MaxBillPlanActions)
	}
	// Bills serve the cooking/food goals, the resource-target goals whose
	// production path (RoundsResourcePlanner.dispatchResourceConcern) stages a
	// bench and then a StockTarget bill on it, the equipment goal whose
	// replacement (RoundsGearPlanner, GearProduce) is a StockTarget bill on
	// a standing bench, and the refrigeration goal whose solar-flare
	// answer is a cook-ahead bill, and the art goal's pinned sculpture
	// bills, and the baby feeding goal's baby food bill, and the mech goal's gestation bills, and the surgery goal's part bills.
	// The work ledger goal commits the Round's whole reconcile plan: places and removals together.
	need, bound := owner.ownerNeed(review)
	bound = bound && (need == policy.EnsureCooking || need == policy.EnsureFoodSupply || need == policy.MaintainFoodStorage || need == policy.MaintainResource || need == policy.MaintainEquipment || need == policy.MaintainRefrigeration || need == policy.MaintainArt || need == policy.MaintainBabyFeeding || need == policy.MaintainMechs || need == policy.MaintainSurgery || need == policy.MaintainWorkLedger)
	if !bound {
		return fmt.Errorf("%w: %s does not admit production bills", ErrConflict, owner.ownerLabel())
	}
	placed := map[[2]string]bool{}
	for _, a := range plan.Actions() {
		if a.Kind() == domain.RemoveProductionBillAction {
			continue
		}
		b, ok := a.ProductionBill()
		key := [2]string{b.Bench(), b.ClaimRecipe()}
		if !ok || placed[key] {
			return fmt.Errorf("%w: bill method mixes action kinds or repeats a bench and recipe", ErrConflict)
		}
		placed[key] = true
	}
	return nil
}
