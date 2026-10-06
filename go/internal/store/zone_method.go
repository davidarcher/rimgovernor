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

func fieldOpenWorkExempt(ctx context.Context, tx *sql.Tx, goal WorkOwner, plan domain.PlanSpec) (bool, error) {
	if len(plan.Actions()) == 0 {
		return false, nil
	}
	for _, action := range plan.Actions() {
		zone, isZone := action.ZoneCreate()
		building, isBuilding := action.Building()
		if isZone && (zone.Kind() == domain.GrowingZone || zone.Kind() == domain.FishingZone) || isBuilding && fieldInfrastructure[building.Definition()] {
			continue
		}
		return false, nil
	}
	for _, m := range goal.OwnerMethods() {
		p, err := load(ctx, tx, m.Plan)
		if err != nil {
			return false, err
		}
		for _, progress := range p.Progress {
			kind := progress.Action().Kind()
			if kind != domain.AcquisitionAction && kind != domain.ProductionBillAction && !butcherSpotBuilding(progress.Action()) && domain.StandardWorkOpen([]domain.Progress{progress}) {
				return false, nil
			}
		}
	}
	return true, nil
}

func admitZoneMethod(ctx context.Context, tx *sql.Tx, owner methodOwner, plan domain.PlanSpec) error {
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
	review, err := loadRounds(ctx, tx)
	if err != nil {
		return err
	}
	// A growing-field method places a bounded batch (EnsureFoodSupply) or the
	// social crops (MaintainResource). A stockpile zone is admitted for any
	// concern that owns stores (policy.StockpileZoneLimit), bounded per owner.
	limit := 32
	if !review.Enabled || review.Snapshot != owner.ownerSnapshot() {
		return ErrConflict
	}
	bound := false
	resource := false
	if need, ok := owner.ownerNeed(review); ok {
		if stockpile {
			limit = policy.StockpileZoneLimit(need)
			bound = limit > 0
		} else {
			bound = need == policy.EnsureFoodSupply || need == policy.MaintainResource
			resource = need == policy.MaintainResource
		}
	}
	if !bound || len(plan.Actions()) > limit {
		return ErrConflict
	}
	cells := map[domain.Cell]bool{}
	cropCells := map[string]int{}
	for _, action := range plan.Actions() {
		zone, ok := action.ZoneCreate()
		if !ok || stockpile != (zone.Kind() == domain.StockpileZone) {
			return ErrConflict
		}
		if resource {
			if zone.Kind() != domain.GrowingZone {
				return ErrConflict
			}
			// Any resource crop may be sown (#2285); the social crops keep their
			// brewing gate and nine-cell ceiling.
			if social := zone.Crop() == "Plant_Hops" || zone.Crop() == "Plant_Smokeleaf"; social {
				cropCells[zone.Crop()] += len(zone.Cells())
				if !review.BrewingFinished || cropCells[zone.Crop()] > 9 {
					return ErrConflict
				}
			}
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
// method per grower per Episode.
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
