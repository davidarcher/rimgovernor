package store

import (
	"context"
	"database/sql"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// butcherSpotDefinition is the free, instant bench EnsureFoodSupply builds
// so a hunt's corpse can be butchered (#260).
const butcherSpotDefinition = "ButcherSpot"

// foodGoal reports the routine EnsureFoodSupply goal; the routine goal id
// ends in its need.
func foodGoal(goal GoalState) bool {
	return goal.Goal.Source == domain.AutopilotGoal && strings.HasSuffix(string(goal.Goal.ID), "-"+string(policy.EnsureFoodSupply))
}

// butcherSpotBuilding reports a building action placing the butcher spot.
func butcherSpotBuilding(action domain.Action) bool {
	building, ok := action.Building()
	return ok && building.Definition() == butcherSpotDefinition
}

// foodFacilityOpenWorkExempt (#260) lets EnsureFoodSupply's butcher spot and
// its production bills be committed while the goal's fields, foraging and
// hunts stay open: those methods run for days and the spot and bill are
// the hunt row's precondition, so waiting on them would never end. A plan
// made only of butcher-spot placements is blocked only by an open
// butcher-spot placement; a plan made only of production bills only by an
// open bill. Any other goal keeps the ordinary rule.
func foodFacilityOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 || !foodGoal(goal) {
		return false, nil
	}
	spot, bill := false, false
	for _, action := range plan.Actions() {
		switch {
		case butcherSpotBuilding(action):
			spot = true
		case action.Kind() == domain.ProductionBillAction:
			bill = true
		case action.Kind() == domain.ZoneCreateAction:
			z, _ := action.ZoneCreate()
			if z.Kind() != domain.StockpileZone || z.Preset() != domain.NothingPreset {
				return false, nil
			}
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
			own := spot && butcherSpotBuilding(progress.Action()) || bill && progress.Action().Kind() == domain.ProductionBillAction
			if existing, ok := progress.Action().ProductionBill(); ok && existing.Mode() == domain.ButcherForever {
				for _, action := range plan.Actions() {
					if proposed, ok := action.ProductionBill(); ok && proposed.Mode() == domain.HumanButcherForever {
						own = false
					}
					if _, ok := action.ZoneCreate(); ok {
						own = false
					}
				}
			}
			if own && domain.GoalWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}
