package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"strings"
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
	pest := pestGoal(goal)
	// EnsureFoodSupply's hunt-only plan passes the goal's open plant
	// harvests (#260): a forage batch runs for days and the hunt rows
	// the butcher spot and bill were placed for would otherwise wait
	// behind it. A hunt still open blocks the next hunt plan.
	hunts := foodGoal(goal)
	for _, action := range plan.Actions() {
		hunts = hunts && huntAcquisition(action)
	}
	for _, m := range goal.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			action := progress.Action()
			if acquisitionIndependentWork(action) || pest && action.Kind() == domain.AcquisitionAction {
				continue
			}
			if hunts && action.Kind() == domain.AcquisitionAction && !huntAcquisition(action) {
				continue
			}
			if domain.GoalWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}

// huntAcquisition reports an acquisition whose output is a corpse: the
// native hunt census names its resource by the prey's corpse definition.
func huntAcquisition(action domain.Action) bool {
	acquisition, ok := action.Acquisition()
	return ok && strings.HasPrefix(acquisition.Definition(), "Corpse_")
}

// pestGoal reports the routine ClearPests goal (#247), whose hunts are
// planned animal by animal: a hunt still awaiting its kill never blocks
// the next animal's method. The routine goal id ends in its need.
func pestGoal(goal GoalState) bool {
	return goal.Goal.Source == domain.AutopilotGoal && strings.HasSuffix(string(goal.Goal.ID), "-"+string(policy.ClearPests))
}

// acquisitionIndependentWork reports the action kinds whose open progress does
// not block a fresh acquisition method for the same goal: a bill, a growing
// zone, or the butcher spot being built for the hunt's corpse (#260).
func acquisitionIndependentWork(action domain.Action) bool {
	if action.Kind() == domain.ProductionBillAction || butcherSpotBuilding(action) {
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
		bound = bound || (binding.Need == policy.MaintainWood || binding.Need == policy.EnsureFoodSupply || binding.Need == policy.MaintainMedicalReserves || binding.Need == policy.ClearPests) && binding.Goal == goal.Goal.ID
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
