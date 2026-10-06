package store

import (
	"context"
	"database/sql"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// admitSupplyMethod admits only stacks the review's cohort still lists, each
// at the cell the census last reported, in bounded batches.
func admitSupplyMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	hasSupply := false
	for _, action := range plan.Actions() {
		hasSupply = hasSupply || action.Kind() == domain.SupplyAllowAction || action.Kind() == domain.SupplyForbidAction
	}
	if !hasSupply {
		return nil
	}
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() || len(plan.Actions()) > 8 {
		return ErrConflict
	}
	bound := false
	reserve := map[string]ReserveSupply{}
	var cohort []policy.StartingSupply
	if need, ok := owner.ownerNeed(review); ok {
		switch need {
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
func reserveAccessOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return false, err
	}
	bound := false
	for _, binding := range review.Standards {
		bound = bound || string(binding.Standard) == goal.OwnerID() && binding.Concern == policy.MaintainFoodStorage
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
	for _, method := range goal.OwnerMethods() {
		existing, err := load(ctx, tx, method.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range existing.Progress {
			if progress.Action().Kind() != domain.ProductionBillAction && domain.StandardWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}
