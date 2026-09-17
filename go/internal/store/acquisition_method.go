package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// acquisitionOpenWorkExempt lets a purely-acquisition method be committed
// alongside a goal's already-dispatched production bill or growing field:
// the bill may be waiting on exactly the ingredient this acquisition fetches,
// and a sown field feeds the colony on a different horizon than harvesting
// wild food does, so neither's open progress blocks the acquisition the way
// any other family's open work would. Any other open work still blocks, same
// as every other goal-method family.
func acquisitionOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.AcquisitionAction {
			return false, nil
		}
	}
	for _, m := range goal.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			if !acquisitionIndependentWork(progress.Action()) && domain.GoalWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}

// acquisitionIndependentWork reports the action kinds whose open progress does
// not block a fresh acquisition method for the same goal.
func acquisitionIndependentWork(action domain.Action) bool {
	if action.Kind() == domain.ProductionBillAction {
		return true
	}
	zone, ok := action.ZoneCreate()
	return ok && zone.Kind() == domain.GrowingZone
}

func admitAcquisitionMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasAcquisition := false
	for _, action := range plan.Actions() {
		hasAcquisition = hasAcquisition || action.Kind() == domain.AcquisitionAction
	}
	if !hasAcquisition {
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
		bound = bound || (binding.Need == policy.MaintainWood || binding.Need == policy.EnsureFoodSupply || binding.Need == policy.MaintainMedicalReserves) && binding.Goal == goal.Goal.ID
	}
	if !bound {
		return ErrConflict
	}
	sources := map[string]bool{}
	for _, action := range plan.Actions() {
		w, ok := action.Acquisition()
		if !ok || sources[w.Thing()] {
			return ErrConflict
		}
		sources[w.Thing()] = true
	}
	return nil
}
