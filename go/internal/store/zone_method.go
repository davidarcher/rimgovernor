package store

import (
	"context"
	"database/sql"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// fieldOpenWorkExempt is the mirror of acquisitionOpenWorkExempt: a method made
// only of growing-zone creations or farm infrastructure may be committed while the goal's open work is
// nothing but dispatched acquisitions or production bills. Sowing a field and
// gathering wild food are independent answers to the same food deficit, and
// the acquisition batch is refreshed every review, so waiting for it to drain
// would starve the field planner indefinitely. Open zone work (an earlier
// field batch still being created) or any other family's open work still
// blocks, so field batches remain strictly sequential.

// fieldInfrastructure are the buildings a field batch may place instead of
// zones: they light, heat or replace the soil a later batch plants.
var fieldInfrastructure = map[string]bool{"SunLamp": true, "HydroponicsBasin": true, "Heater": true}

func fieldOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, action := range plan.Actions() {
		zone, isZone := action.ZoneCreate()
		building, isBuilding := action.Building()
		if isZone && zone.Kind() == domain.GrowingZone || isBuilding && fieldInfrastructure[building.Definition()] {
			continue
		}
		return false, nil
	}
	for _, m := range goal.Methods {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			kind := progress.Action().Kind()
			if kind != domain.AcquisitionAction && kind != domain.ProductionBillAction && domain.GoalWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}

func admitZoneMethod(ctx context.Context, tx *sql.Tx, goal GoalState, plan domain.PlanSpec) error {
	hasWork := false
	stockpile := false
	for _, action := range plan.Actions() {
		if zone, ok := action.ZoneCreate(); ok {
			hasWork = true
			stockpile = stockpile || zone.Kind() == domain.StockpileZone
		}
	}
	if !hasWork {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	// A stockpile zone is created one per method in this slice, unlike the
	// bounded batches of growing-field zones EnsureFoodSupply may dispatch.
	// EnsureFoodStorage places the colony's food stockpile; SecureSupplies
	// places its covered-storage fallback (routine_secure_supplies.go);
	// MaintainResource places the production ladder's ingredient stockpile
	// beside the bench (routine_ingredient_storage.go, #155: the rung was
	// refused here on every live run before it was bound); MaintainAnimalFeed
	// places the feed stockpile inside the animals' area when no bench is
	// reachable there (routine_animal_feed.go, #311: refused here the same
	// way until bound).
	limit := 32
	needs := []policy.GoalID{policy.EnsureFoodSupply}
	if stockpile {
		limit = 1
		needs = []policy.GoalID{policy.EnsureFoodStorage, policy.SecureSupplies, policy.MaintainResource, policy.MaintainAnimalFeed}
	}
	if !review.Enabled || review.Snapshot != goal.Goal.Snapshot || goal.Goal.Source != domain.AutopilotGoal || len(plan.Actions()) > limit {
		return ErrConflict
	}
	bound := false
	for _, binding := range review.Goals {
		if binding.Goal != goal.Goal.ID {
			continue
		}
		for _, need := range needs {
			bound = bound || binding.Need == need
		}
	}
	if !bound {
		return ErrConflict
	}
	cells := map[domain.Cell]bool{}
	for _, action := range plan.Actions() {
		zone, ok := action.ZoneCreate()
		if !ok || stockpile != (zone.Kind() == domain.StockpileZone) {
			return ErrConflict
		}
		for _, cell := range zone.Cells() {
			if cells[cell] {
				return ErrConflict
			}
			cells[cell] = true
		}
	}
	return nil
}

// growerCropOpenWorkExempt: a method made only of grower re-crops may be
// committed under any open work. It re-crops a grower that already stands,
// shares no cells or stock with the basin batch that built it, and that
// batch stays open until its last basin does; the planner commits one such
// method per grower per goal epoch.
func growerCropOpenWorkExempt(plan domain.PlanSpec) bool {
	if len(plan.Actions()) == 0 {
		return false
	}
	for _, action := range plan.Actions() {
		if action.Kind() != domain.GrowerCropAction {
			return false
		}
	}
	return true
}
