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
	season, seasonKnown := r.Climate.DaysRemaining.Value()
	fraction, fk := r.Coverage.Value()
	if !sk || !sowing || len(r.Choices) > 256 || !fk || !foodNumber(fraction) || seasonKnown && !fieldPositive(season) {
		return FieldPlan{}, false
	}
	seen := map[string]bool{}
	for _, crop := range r.Choices {
		if seen[crop.Name] {
			return FieldPlan{}, false
		}
		seen[crop.Name] = true
	}
	type viable struct {
		crop   CropChoice
		days   float64
		needed int
	}
	var viables []viable
	plan := FieldPlan{}
	excluded := func(crop CropChoice, reason string) {
		plan.Candidates = append(plan.Candidates, FieldCandidate{Crop: crop, Reason: reason})
	}
	for _, crop := range r.Choices {
		available, ak := crop.Available.Value()
		edible, ek := crop.Edible.Value()
		days, gk := crop.GrowDays.Value()
		switch {
		case !ak || !ek || !gk || !fieldPositive(days):
			excluded(crop, "incomplete crop facts")
			continue
		case !available || !edible:
			excluded(crop, "not an available edible crop")
			continue
		case seasonKnown && days*2.5 > season:
			excluded(crop, "season too short")
			continue
		}
		count, known := FieldTarget(r.Colonists, crop, r.ReserveDays).Value()
		if !known {
			excluded(crop, "field target unknown")
			continue
		}
		needed := int(math.Ceil(float64(count)*(1-fraction) - 1e-9))
		if needed <= 0 {
			return FieldPlan{}, false
		}
		viables = append(viables, viable{crop, days, needed})
	}
	if len(viables) == 0 {
		return plan, false
	}
	fastest := viables[0].days
	for _, v := range viables {
		fastest = min(fastest, v.days)
	}
	foodDays, runwayKnown := r.Runway.Value()
	urgent := !seasonKnown || runwayKnown && foodNumber(foodDays) && foodDays < fastest*2.5
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
		a, b := plan.Candidates[i], plan.Candidates[j]
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
	})
	best := plan.Candidates[0]
	if best.Sites.Cells == 0 {
		return plan, false
	}
	plan.Crop, plan.Needed, plan.Sites = best.Crop, best.Needed, best.Sites
	return plan, true
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
