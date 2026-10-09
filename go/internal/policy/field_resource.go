package policy

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceFieldRequest is a demand-driven field decision: sow the
// crop whose harvest is Resource so that Deficit units come in. Edibility is
// irrelevant; only Harvests and UnitsPerCell say what a crop is for.
type ResourceFieldRequest struct {
	Resource Resource
	// Deficit is the units still wanted; unknown or non-positive plans nothing.
	Deficit domain.Fact[float64]
	Choices []CropChoice
	Climate CropClimate
	Site    FarmSiteRequest
	// GrowerSkill is the best Plants level among the colonists who sow
	// (GrowerSkill); a crop with a sow skill floor is planned only when it is
	// known and meets it.
	GrowerSkill domain.Fact[int32]
}

// PlanFieldByResource chooses among the available crops whose Harvests is the
// demanded resource and sites patches for the best one. It shares the food
// planner's viability rules (season of 2.5 grow cycles, fertility floor,
// glow, dark biome, pollution) and patch picker, with cells needed =
// ceil(deficit / UnitsPerCell); the patch picker
// then fits it to the observed arable cells. Unlike food it
// never plants on a guess: unknown season, darkness or crop facts refuse.
// Candidates are ranked by yield per day over the needed cells.
//
// A tree species (IsTreeCrop) is planned on the native lattice, one
// tree per domain.TreeCellsPerTree cells, each yielding its wood at the first
// harvestable growth (treePricing); it needs a researched, biome-native species
// and a skilled sower, has no season to fit (its growth carries across
// seasons), and ranks by price (wood per cell-day of land).
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
		v, reason := resourceCropViable(crop, deficit, season, r)
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

// ResourceFieldPrefix starts the ID of a resource field candidate: the prefix
// plus the crop name, so the executor reads the plan it priced by ID.
const ResourceFieldPrefix = "field:"

// ResourceFieldCandidate prices a planned resource field as a harvest
// candidate: the plan's cells, the crop's grow days and units per cell,
// the sowing as upfront labor (FieldSowTicksPerCell) and the harvest work over
// the grow time as daily labor. False when the plan has no cells or a crop fact
// is unknown; a field is never priced on a guess.
func ResourceFieldCandidate(resource Resource, plan FieldPlan) (SupplyCandidate, bool) {
	days, dk := plan.Crop.GrowDays.Value()
	units, uk := plan.Crop.UnitsPerCell.Value()
	work, wk := plan.Crop.HarvestWork.Value()
	cells := plan.Sites.Cells
	if IsTreeCrop(plan.Crop) {
		return treeFieldCandidate(resource, plan)
	}
	if !dk || !uk || !wk || !fieldPositive(days) || !fieldPositive(units) || work < 0 || cells <= 0 {
		return SupplyCandidate{}, false
	}
	return FieldHarvestCandidate(resource, ResourceFieldPrefix+plan.Crop.Name, FieldHarvest{
		Cells: int64(cells), GrowDays: days, UnitsPerCell: units,
		SetupTicks: float64(cells) * FieldSowTicksPerCell, WorkPerDay: work * float64(cells) / days,
	})
}

// treeFieldCandidate prices a planned tree plantation: its trees' sowing as
// upfront labor, the days to the fell point as lead, and the felling over those
// days as daily labor, each tree giving its wood at that point.
func treeFieldCandidate(resource Resource, plan FieldPlan) (SupplyCandidate, bool) {
	fraction, wood, _, ok := treePricing(plan.Crop)
	days, _ := plan.Crop.GrowDays.Value()
	sow, _ := plan.Crop.SowWork.Value()
	fell, _ := plan.Crop.HarvestWork.Value()
	trees := plan.Sites.Cells / domain.TreeCellsPerTree
	if !ok || trees <= 0 {
		return SupplyCandidate{}, false
	}
	return FieldHarvestCandidate(resource, ResourceFieldPrefix+plan.Crop.Name, FieldHarvest{
		Cells: int64(trees) * domain.TreeCellsPerTree, GrowDays: days * fraction, UnitsPerCell: wood / domain.TreeCellsPerTree,
		SetupTicks: float64(trees) * sow, WorkPerDay: fell * float64(trees) / (days * fraction),
	})
}

// StandingFieldYield is the units one standing field of crop on cells usable
// cells delivers per harvest toward resource: zero when the crop harvests
// something else or a fact is unknown. A tree zone counts its lattice trees at
// the wood each gives at the fell point.
func StandingFieldYield(crop CropChoice, resource Resource, cells domain.Fact[uint32]) float64 {
	harvests, hk := crop.Harvests.Value()
	units, uk := crop.UnitsPerCell.Value()
	n, nk := cells.Value()
	if IsTreeCrop(crop) && hk && harvests == resource && nk {
		if _, wood, _, ok := treePricing(crop); ok {
			return wood * float64(n/domain.TreeCellsPerTree)
		}
		return 0
	}
	if !hk || !uk || !nk || harvests != resource || !fieldPositive(units) {
		return 0
	}
	return units * float64(n)
}

// resourceCropViable screens one crop that harvests the demanded resource,
// returning the viable crop or the reason it is excluded.
func resourceCropViable(crop CropChoice, deficit, season float64, r ResourceFieldRequest) (viableCrop, string) {
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
	}
	if reason := cropSowable(crop, r.GrowerSkill); reason != "" {
		return viableCrop{}, reason
	}
	if IsTreeCrop(crop) {
		return treeViable(crop, deficit, r)
	}
	if days*fieldCycles > season {
		return viableCrop{}, "season too short"
	}
	needed := int(math.Ceil(deficit/units - 1e-9))
	if needed <= 0 {
		return viableCrop{}, fmt.Sprintf("no cells needed for %.2f units", deficit)
	}
	return viableCrop{crop: crop, days: days, needed: needed, units: units}, ""
}

// treeViable sizes a tree plantation for deficit units: whole lattice trees of
// the wood one gives at the fell point, domain.TreeCellsPerTree cells each.
func treeViable(crop CropChoice, deficit float64, r ResourceFieldRequest) (viableCrop, string) {
	if reason := treeSowable(crop, r.Climate.Biome, r.GrowerSkill); reason != "" {
		return viableCrop{}, reason
	}
	_, wood, price, ok := treePricing(crop)
	if !ok {
		return viableCrop{}, "incomplete crop facts"
	}
	trees := int(math.Ceil(deficit/wood - 1e-9))
	if trees <= 0 {
		return viableCrop{}, fmt.Sprintf("no trees needed for %.2f units", deficit)
	}
	days, _ := crop.GrowDays.Value()
	return viableCrop{crop: crop, days: days, needed: trees * domain.TreeCellsPerTree, units: wood / domain.TreeCellsPerTree, price: price}, ""
}
