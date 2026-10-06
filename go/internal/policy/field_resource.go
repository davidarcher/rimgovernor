package policy

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceFieldCellCap bounds the cells one resource field plan asks for.
const ResourceFieldCellCap = 4096

// ResourceFieldRequest is a demand-driven field decision (#2283): sow the
// crop whose harvest is Resource so that Deficit units come in. Edibility is
// irrelevant; only Harvests and UnitsPerCell (#2282) say what a crop is for.
type ResourceFieldRequest struct {
	Resource Resource
	// Deficit is the units still wanted; unknown or non-positive plans nothing.
	Deficit domain.Fact[float64]
	Choices []CropChoice
	Climate CropClimate
	Site    FarmSiteRequest
}

// PlanFieldByResource chooses among the available crops whose Harvests is the
// demanded resource and sites patches for the best one. It shares the food
// planner's viability rules (season of 2.5 grow cycles, fertility floor,
// glow, dark biome, pollution) and patch picker, with cells needed =
// ceil(deficit / UnitsPerCell) up to ResourceFieldCellCap. Unlike food it
// never plants on a guess: unknown season, darkness or crop facts refuse.
// Candidates are ranked by yield per day over the needed cells.
//
// Seam for trees (#2289): a crop whose HarvestDestroys is true leaves the
// cell empty after harvest, and its lattice density and fell yield differ;
// both belong in resourceCropViable (units, needed) and not in the ranking.
func PlanFieldByResource(r ResourceFieldRequest) (FieldPlan, bool) {
	deficit, dk := r.Deficit.Value()
	sowing, sk := r.Climate.SowingOutdoors().Value()
	season, ck := r.Climate.DaysRemaining.Value()
	if !dk || !fieldPositive(deficit) || !sk || !sowing || !ck || !fieldPositive(season) || r.Resource == "" {
		return FieldPlan{}, false
	}
	seen := map[string]bool{}
	plan := FieldPlan{}
	var viables []viableCrop
	for _, crop := range r.Choices {
		if seen[crop.Name] {
			return FieldPlan{}, false
		}
		seen[crop.Name] = true
		resource, rk := crop.Harvests.Value()
		if rk && resource != r.Resource {
			continue
		}
		v, reason := resourceCropViable(crop, deficit, season)
		if reason != "" {
			plan.Candidates = append(plan.Candidates, FieldCandidate{Crop: crop, Reason: reason})
			continue
		}
		viables = append(viables, v)
	}
	if len(viables) == 0 {
		return plan, false
	}
	req := FieldRequest{Site: r.Site}
	for _, v := range viables {
		plan.Candidates = append(plan.Candidates, fieldCandidate(req, v, false))
	}
	return rankField(plan, false)
}

// resourceCropViable screens one crop that harvests the demanded resource,
// returning the viable crop or the reason it is excluded.
func resourceCropViable(crop CropChoice, deficit, season float64) (viableCrop, string) {
	available, ak := crop.Available.Value()
	days, gk := crop.GrowDays.Value()
	units, uk := crop.UnitsPerCell.Value()
	_, rk := crop.Harvests.Value()
	switch {
	case !rk || !ak || !gk || !uk || !fieldPositive(days) || !fieldPositive(units):
		return viableCrop{}, "incomplete crop facts"
	case !available:
		return viableCrop{}, "crop not available"
	case GrowsInDark(crop):
		return viableCrop{}, "crop needs a dark room"
	case days*fieldCycles > season:
		return viableCrop{}, "season too short"
	}
	needed := int(math.Min(ResourceFieldCellCap, math.Ceil(deficit/units-1e-9)))
	if needed <= 0 {
		return viableCrop{}, fmt.Sprintf("no cells needed for %.2f units", deficit)
	}
	return viableCrop{crop: crop, days: days, needed: needed, units: units}, ""
}
