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
func acquisitionOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	// A stall withdraw is admitted over any open work: it only
	// removes a designation nobody took.
	if _, ok := plan.Actions()[0].AcquisitionWithdraw(); ok && len(plan.Actions()) == 1 {
		return true, nil
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.AcquisitionAction {
			return false, nil
		}
	}
	pest := pestConcern(goal)
	// EnsureFoodSupply's hunt-only plan passes the goal's open plant
	// harvests: a forage batch runs for days and the hunt rows
	// the butcher spot and bill were placed for would otherwise wait
	// behind it. The hunt count already nets out the designated hunts
	// (pending nutrition, the hunters' budget), so open acquisition
	// work of any kind does not block a hunt plan.
	hunts := foodConcern(goal)
	for _, action := range plan.Actions() {
		hunts = hunts && huntAcquisition(action)
	}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			action := progress.Action()
			if acquisitionIndependentWork(action) || pest && action.Kind() == domain.AcquisitionAction {
				continue
			}
			if hunts && action.Kind() == domain.AcquisitionAction {
				continue
			}
			if domain.StandardWorkOpen([]domain.Progress{progress}) {
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
	return ok && acquisition.Hunt()
}

// pestGoal reports the routine ClearPests goal, whose hunts are
// planned animal by animal: a hunt still awaiting its kill never blocks
// the next animal's method. The routine goal id names its need.
func pestConcern(goal WorkOwner) bool {
	return roundsStandardOwns(domain.ConcernID(goal.OwnerID()), policy.ClearPests)
}

// acquisitionIndependentWork reports the action kinds whose open progress does
// not block a fresh acquisition method for the same goal: a bill, a growing
// zone, or the butcher spot being built for the hunt's corpse.
func acquisitionIndependentWork(action domain.Action) bool {
	if action.Kind() == domain.ProductionBillAction || action.Kind() == domain.CommsTradeRequestAction || butcherSpotBuilding(action) {
		return true
	}
	zone, ok := action.ZoneCreate()
	return ok && zone.Kind() == domain.GrowingZone
}

func admitAcquisitionMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
	hasAcquisition := false
	for _, action := range plan.Actions() {
		hasAcquisition = hasAcquisition || action.Kind() == domain.AcquisitionAction
	}
	if !hasAcquisition {
		return nil
	}
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() || len(plan.Actions()) > policy.MaxCatalogSelection+policy.MaxHuntRows {
		return ErrConflict
	}
	need, bound := owner.ownerNeed(review)
	bound = bound && (need == policy.EnsureFoodSupply || need == policy.MaintainMedicalReserves || need == policy.ClearPests || need == policy.MaintainResource)
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
