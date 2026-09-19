package policy

import (
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PenGrazing compares a seasonal horizon with the native pen's worst-season
// pasture rate and stored feed. It does not add grass to the human food ledger.
type PenGrazing struct {
	ID                                           string
	DemandPerDay, PasturePerDay, StoredNutrition domain.Fact[float64]
}

func HayNutritionNeed(pens domain.Fact[[]PenGrazing], gap domain.Fact[float64]) domain.Fact[float64] {
	rows, known := pens.Value()
	days, dk := gap.Value()
	if !known || !dk || !foodNumber(days) {
		return domain.Unknown[float64]()
	}
	need := 0.0
	for _, p := range rows {
		d, dk := p.DemandPerDay.Value()
		y, yk := p.PasturePerDay.Value()
		s, sk := p.StoredNutrition.Value()
		if !dk || !yk || !sk || !foodNumber(d) || !foodNumber(y) || !foodNumber(s) {
			return domain.Unknown[float64]()
		}
		need += math.Max(0, days*(d-y)-s)
	}
	return domain.Known(need)
}

// PlanHayField uses the shared soil planner without declaring hay human-edible.
// Existing hay capacity is subtracted by the caller; unknown or nonnegative
// grazing balance never opens a field.
func PlanHayField(need domain.Fact[float64], crop CropChoice, climate CropClimate, site FarmSiteRequest) (FieldPlan, bool) {
	n, nk := need.Value()
	yield, yk := crop.HarvestNutrition.Value()
	days, dk := crop.GrowDays.Value()
	sowing, sk := climate.Sowing.Value()
	season, ck := climate.DaysRemaining.Value()
	available, ak := crop.Available.Value()
	if !nk || !foodNumber(n) || n <= 0 || crop.Name != "Plant_Haygrass" || !yk || !fieldPositive(yield) || !dk || !fieldPositive(days) || !sk || !sowing || !ck || season < days*fieldCycles || !ak || !available {
		return FieldPlan{}, false
	}
	crop.Edible = domain.Known(false)
	site.Crop, site.Needed = crop, int(math.Min(4096, math.Ceil(n/yield)))
	sites := PlanFarmSites(site)
	return FieldPlan{Crop: crop, Needed: site.Needed, Sites: sites}, sites.Cells > 0
}

type SlaughterFoodAnimal struct {
	ID                                          PawnID
	Race                                        Resource
	MeatNutrition, FeedPerDay, ReproductionDays domain.Fact[float64]
}

// SlaughterFoodChannels offers one safe animal above the protected population.
// Order uses meat per feed/day first, then shorter reproduction interval. The
// ledger still charges the ordinary slaughter work and can decline the offer.
func SlaughterFoodChannels(rows []SlaughterFoodAnimal, animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy) []FoodChannel {
	if !herd.AllowSlaughter {
		return nil
	}
	observed, known := animals.Value()
	if !known {
		return nil
	}
	floors := map[Resource]int64{}
	for _, row := range observed {
		floors[row.Definition] = herd.PopulationMin[row.Definition]
	}
	candidates, unknown := herdSurplusCandidates(observed, floors, domain.HusbandrySlaughter)
	if unknown {
		return nil
	}
	safe := map[PawnID]bool{}
	for _, a := range candidates {
		safe[a.ID] = true
	}
	type scored struct {
		animal                              SlaughterFoodAnimal
		nutrition, efficiency, reproduction float64
	}
	var choices []scored
	for _, a := range rows {
		n, nk := a.MeatNutrition.Value()
		feed, fk := a.FeedPerDay.Value()
		days, dk := a.ReproductionDays.Value()
		if !safe[a.ID] || !nk || !fk || !dk || !fieldPositive(n) || !fieldPositive(feed) || !fieldPositive(days) {
			continue
		}
		choices = append(choices, scored{a, n, n / feed, days})
	}
	sort.Slice(choices, func(i, j int) bool {
		a, b := choices[i], choices[j]
		if a.efficiency != b.efficiency {
			return a.efficiency > b.efficiency
		}
		if a.reproduction != b.reproduction {
			return a.reproduction < b.reproduction
		}
		return a.animal.ID < b.animal.ID
	})
	if len(choices) == 0 {
		return nil
	}
	a := choices[0]
	// JobDriver_Slaughter.SlaughterDuration is 180 ticks. Butchering and
	// hauling remain separate native jobs, as for the hunt channel.
	return []FoodChannel{{Kind: FoodHunt, ID: "slaughter:" + string(a.animal.ID), NutritionPerDay: domain.Known(a.nutrition), WorkPerDay: domain.Known(180.0), LeadDays: domain.Known(0.0), Open: domain.Known(false), Terms: []FoodPlanTerm{{Name: "meat_per_daily_feed", Value: a.efficiency}, {Name: "reproduction_days", Value: a.reproduction}}}}
}

func FoodSlaughterChoice(plan domain.Fact[FoodPlan], animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy) HusbandryChoice {
	if !herd.AllowSlaughter {
		return HusbandryChoice{Reason: HusbandryNoDeficit}
	}
	p, pk := plan.Value()
	rows, rk := animals.Value()
	if !pk || !rk {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	floors := map[Resource]int64{}
	for _, a := range rows {
		floors[a.Definition] = herd.PopulationMin[a.Definition]
	}
	safe, unknown := herdSurplusCandidates(rows, floors, domain.HusbandrySlaughter)
	if unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	for _, e := range p.Portfolio {
		if e.Channel.Kind != FoodHunt || e.Decision != FoodPlanOpen || !strings.HasPrefix(e.Channel.ID, "slaughter:") {
			continue
		}
		for _, a := range safe {
			if e.Channel.ID == "slaughter:"+string(a.ID) {
				return HusbandryChoice{Animal: a.ID, Method: domain.HusbandrySlaughter}
			}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
