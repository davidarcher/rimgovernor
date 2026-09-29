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

// PlanHayField sizes a hay field without declaring hay human-edible; the
// caller sites it in the layout plan's field blocks (#1226). Existing hay capacity is subtracted by the caller; unknown or nonnegative
// grazing balance never opens a field.
func PlanHayField(need domain.Fact[float64], crop CropChoice, climate CropClimate) (FieldPlan, bool) {
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
	return FieldPlan{Crop: crop, Needed: int(math.Min(4096, math.Ceil(n/yield)))}, true
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
	observed, known := animals.Value()
	if !known {
		return nil
	}
	pending := map[PawnID]bool{}
	// Re-price existing orders too. These copies are planning offers only;
	// fresh destructive admission still requires native SafeToSlaughter.
	observed = append([]UpkeepAnimal(nil), observed...)
	for i := range observed {
		a := &observed[i]
		if slaughter, known := a.Slaughter.Value(); known && slaughter {
			pending[a.ID] = true
			a.Slaughter = domain.Known(false)
			a.SafeToSlaughter = domain.Known(true)
		}
	}
	floors := herdFoodLimits(observed, herd)
	candidates, unknown := herdSurplusCandidates(observed, floors, herd.FeedShort)
	if unknown {
		return nil
	}
	safe := map[PawnID]bool{}
	for _, r := range candidates {
		safe[r.animal.ID] = r.method == domain.HusbandrySlaughter
	}
	counts := map[Resource]int64{}
	for _, a := range observed {
		if release, known := a.Release.Value(); known && !release {
			counts[a.Definition]++
		}
	}
	for _, a := range observed {
		if release, known := a.Release.Value(); pending[a.ID] && known && !release && counts[a.Definition] > floors[a.Definition] {
			safe[a.ID] = true
		}
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
		if pending[a.animal.ID] != pending[b.animal.ID] {
			return pending[a.animal.ID]
		}
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
	p, pk := plan.Value()
	rows, rk := animals.Value()
	if !pk || !rk {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	safe, unknown := herdSurplusCandidates(rows, herdFoodLimits(rows, herd), herd.FeedShort)
	if unknown {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	for _, e := range p.Portfolio {
		if e.Channel.Kind != FoodHunt || e.Decision != FoodPlanOpen || !strings.HasPrefix(e.Channel.ID, "slaughter:") {
			continue
		}
		for _, r := range safe {
			if r.method == domain.HusbandrySlaughter && e.Channel.ID == "slaughter:"+string(r.animal.ID) {
				return HusbandryChoice{Animal: r.animal.ID, Method: domain.HusbandrySlaughter}
			}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
