package policy

import (
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type CropClimate struct {
	Sowing        domain.Fact[bool]
	DaysRemaining domain.Fact[float64]
}
type CropChoice struct {
	Name                                                                   string
	Available, Edible                                                      domain.Fact[bool]
	GrowDays, FertilityMin, FertilitySensitivity, HarvestNutrition, Demand domain.Fact[float64]
	// SowTags are the native sow tags ("Ground", "Hydroponic"); MinGlow is the
	// native minimum light for growth, zero for cave crops.
	SowTags domain.Fact[[]string]
	MinGlow domain.Fact[float64]
}

// FieldRequest is one expansion decision: which edible crop to sow, and where,
// so that observed coverage reaches the reserve target.
type FieldRequest struct {
	Choices   []CropChoice
	Climate   CropClimate
	Runway    domain.Fact[float64]
	Colonists domain.Fact[int64]
	// ReserveDays is the stored-food buffer FieldTarget budgets beyond one cycle.
	ReserveDays float64
	// Coverage is the fraction of the target already growing (FieldCoverage).
	Coverage domain.Fact[float64]
	Site     FarmSiteRequest
}

// FieldCandidate is one crop's plan over the soil it would actually use.
// Score is the plan's net nutrition per day divided by the cells the crop
// needs, so a crop that cannot fill its own target is penalized.
type FieldCandidate struct {
	Crop   CropChoice
	Needed int
	Sites  FarmSitePlan
	Score  float64
	Urgent bool
	Reason string
}

type FieldPlan struct {
	Crop       CropChoice
	Needed     int
	Sites      FarmSitePlan
	Urgent     bool
	Candidates []FieldCandidate
}

func (p FieldPlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%s needed=%d urgent=%t", p.Crop.Name, p.Needed, p.Urgent)
	for _, c := range p.Candidates {
		fmt.Fprintf(&b, "\n %s needed=%d cells=%d score=%.4f %s", c.Crop.Name, c.Needed, c.Sites.Cells, c.Score, c.Reason)
	}
	return b.String()
}

// PlanField chooses the crop and patches jointly. Every available edible crop
// with complete native facts is a candidate; its patches come from
// PlanFarmSites over the crop's own fertility floor, so a fertility-tolerant
// crop wins on poor soil where a richer crop would find no land. A remaining
// season shorter than 2.5 grow cycles excludes a crop; an unknown remaining
// season while sowing is possible is treated as short, so the fastest crop
// that can plant is chosen rather than nothing. A stored-food runway shorter
// than 2.5 cycles of the fastest crop is urgent and also prefers the fastest
// crop. Existing zones are never re-cropped: the plan only adds patches.
func PlanField(r FieldRequest) (FieldPlan, bool) {
	sowing, sk := r.Climate.Sowing.Value()
	if !sk || !sowing {
		return FieldPlan{}, false
	}
	return planField(r, false)
}

type viableCrop struct {
	crop   CropChoice
	days   float64
	needed int
}

// viableCrops screens the request's crops. With indoor set the remaining
// outdoor season is ignored: a roofed, lit and heated site grows all year.
func viableCrops(r FieldRequest, indoor bool) (viables []viableCrop, excluded []FieldCandidate, ok bool) {
	season, seasonKnown := r.Climate.DaysRemaining.Value()
	fraction, fk := r.Coverage.Value()
	if len(r.Choices) > 256 || !fk || !foodNumber(fraction) || seasonKnown && !fieldPositive(season) {
		return nil, nil, false
	}
	seen := map[string]bool{}
	for _, crop := range r.Choices {
		if seen[crop.Name] {
			return nil, nil, false
		}
		seen[crop.Name] = true
	}
	exclude := func(crop CropChoice, reason string) {
		excluded = append(excluded, FieldCandidate{Crop: crop, Reason: reason})
	}
	for _, crop := range r.Choices {
		available, ak := crop.Available.Value()
		edible, ek := crop.Edible.Value()
		days, gk := crop.GrowDays.Value()
		switch {
		case !ak || !ek || !gk || !fieldPositive(days):
			exclude(crop, "incomplete crop facts")
			continue
		case !available || !edible:
			exclude(crop, "not an available edible crop")
			continue
		case !indoor && seasonKnown && days*2.5 > season:
			exclude(crop, "season too short")
			continue
		}
		count, known := FieldTarget(r.Colonists, crop, r.ReserveDays).Value()
		if !known {
			exclude(crop, "field target unknown")
			continue
		}
		needed := int(math.Ceil(float64(count)*(1-fraction) - 1e-9))
		if needed <= 0 {
			return nil, nil, false
		}
		viables = append(viables, viableCrop{crop, days, needed})
	}
	return viables, excluded, true
}

// fieldUrgency is true when the stored-food runway is shorter than 2.5 cycles
// of the fastest viable crop, or (outdoors) when the remaining season is
// unknown while sowing is possible.
func fieldUrgency(r FieldRequest, viables []viableCrop, indoor bool) bool {
	if len(viables) == 0 {
		return false
	}
	fastest := viables[0].days
	for _, v := range viables {
		fastest = min(fastest, v.days)
	}
	_, seasonKnown := r.Climate.DaysRemaining.Value()
	foodDays, runwayKnown := r.Runway.Value()
	return !indoor && !seasonKnown || runwayKnown && foodNumber(foodDays) && foodDays < fastest*2.5
}

func planField(r FieldRequest, indoor bool) (FieldPlan, bool) {
	viables, excluded, ok := viableCrops(r, indoor)
	if !ok {
		return FieldPlan{}, false
	}
	plan := FieldPlan{Candidates: excluded}
	if len(viables) == 0 {
		return plan, false
	}
	urgent := fieldUrgency(r, viables, indoor)
	plan.Urgent = urgent
	for _, v := range viables {
		site := r.Site
		site.Crop = v.crop
		site.Needed = v.needed
		sites := PlanFarmSites(site)
		c := FieldCandidate{Crop: v.crop, Needed: v.needed, Sites: sites, Urgent: urgent}
		if sites.Cells == 0 {
			c.Reason = "no plantable soil"
		} else {
			total := 0.0
			for _, s := range sites.Selected {
				total += s.Score
			}
			c.Score = total / float64(v.needed)
			c.Reason = fmt.Sprintf("net %.4f/day over %d patches", total, len(sites.Patches))
		}
		plan.Candidates = append(plan.Candidates, c)
	}
	sort.Slice(plan.Candidates, func(i, j int) bool {
		return fieldCandidateLess(plan.Candidates[i], plan.Candidates[j], urgent)
	})
	best := plan.Candidates[0]
	if best.Sites.Cells == 0 {
		return plan, false
	}
	plan.Crop, plan.Needed, plan.Sites = best.Crop, best.Needed, best.Sites
	return plan, true
}

// fieldCandidateLess orders plantable candidates first, then the fastest crop
// under urgency, then the highest score, then the fastest crop, then by name.
func fieldCandidateLess(a, b FieldCandidate, urgent bool) bool {
	if (a.Sites.Cells > 0) != (b.Sites.Cells > 0) {
		return a.Sites.Cells > 0
	}
	ad, _ := a.Crop.GrowDays.Value()
	bd, _ := b.Crop.GrowDays.Value()
	if urgent && ad != bd {
		return ad < bd
	}
	if a.Score != b.Score {
		return a.Score > b.Score
	}
	if ad != bd {
		return ad < bd
	}
	return a.Crop.Name < b.Crop.Name
}

func freeCropSoil(cell SiteCell, minimum float64, blocked map[domain.Cell]bool) (float64, bool) {
	walk, wk := cell.Walkable.Value()
	occupied, ok := cell.Occupied.Value()
	zone, zk := cell.Zone.Value()
	roof, rk := cell.Roofed.Value()
	soil, fk := cell.Fertility.Value()
	return soil, !blocked[cell.Cell] && wk && walk && ok && !occupied && zk && !zone && rk && !roof && fk && foodNumber(soil) && soil >= minimum
}

func FieldTarget(colonists domain.Fact[int64], crop CropChoice, reserve float64) domain.Fact[int] {
	count, ck := colonists.Value()
	demand, dk := crop.Demand.Value()
	days, gk := crop.GrowDays.Value()
	yield, yk := crop.HarvestNutrition.Value()
	if !ck || count < 0 || !dk || !gk || !yk || !fieldPositive(demand) || !fieldPositive(days) || !fieldPositive(yield) || !fieldPositive(reserve) {
		return domain.Unknown[int]()
	}
	target := math.Max(float64(count)*10, math.Ceil(demand*(days*2.5+reserve)/yield))
	if !fieldPositive(target) || target > 65536 {
		return domain.Unknown[int]()
	}
	return domain.Known(int(target))
}
