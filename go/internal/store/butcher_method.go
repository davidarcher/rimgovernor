package store

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// butcherSpotDefinition is the free, instant bench EnsureFoodSupply builds
// so a hunt's corpse can be butchered.
const butcherSpotDefinition = "ButcherSpot"

// foodStandard reports the routine EnsureFoodSupply goal; the routine goal id
// ends in its need.
func foodConcern(goal StandardState) bool {
	return goal.Standard.Kind == policy.EnsureFoodSupply
}

// butcherBenchAction reports an action placing or removing a butcher bench:
// the spot or the table in the planned butchery, or the spot's deconstruction
// once the table stands.
func butcherBenchAction(action domain.Action) bool {
	if building, ok := action.Building(); ok {
		return building.Definition() == butcherSpotDefinition || building.Definition() == policy.ButcherTableDefinition
	}
	cut, ok := action.Deconstruction()
	return ok && cut.Definition() == butcherSpotDefinition
}

// butcherFacilityWork reports an action of the butcher facility under method:
// a bench action, or any action of the planned butchery's ring wave.
func butcherFacilityWork(method domain.MethodID, action domain.Action) bool {
	return IsRoomShellMethod(method) || butcherBenchAction(action)
}

// foodFacilityOpenWorkExempt lets EnsureFoodSupply's butcher facility (the
// spot, the table, the planned butchery's ring and the spot's removal) and its
// production bills be committed while the goal's fields, foraging and hunts
// stay open: those methods run for days and the spot and bill are the hunt
// row's precondition, so waiting on them would never end. A plan made only of
// facility actions is blocked only by an open facility action of the same kind
// of method (a ring wave and the bench run side by side); a plan made only of
// production bills only by an open bill. Any other goal keeps the ordinary
// rule.
func foodFacilityOpenWorkExempt(ctx context.Context, tx *Tx, goal StandardState, method domain.MethodID, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 || !foodConcern(goal) {
		return false, nil
	}
	spot, bill := false, false
	ring := IsRoomShellMethod(method)
	for _, action := range plan.Actions() {
		switch {
		case butcherFacilityWork(method, action):
			spot = true
		case action.Kind() == domain.ProductionBillAction:
			bill = true
		default:
			return false, nil
		}
	}
	if spot == bill {
		return false, nil
	}
	for _, m := range goal.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			own := spot && butcherFacilityWork(m.Method, progress.Action()) && IsRoomShellMethod(m.Method) == ring || bill && progress.Action().Kind() == domain.ProductionBillAction
			if existing, ok := progress.Action().ProductionBill(); ok && existing.Mode() == domain.ButcherForever {
				for _, action := range plan.Actions() {
					if proposed, ok := action.ProductionBill(); ok && proposed.Mode() == domain.HumanButcherForever {
						own = false
					}
				}
			}
			if own && domain.StandardWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}
