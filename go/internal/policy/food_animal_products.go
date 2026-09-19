package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AnimalProduct is one observed production comp, not harvested stock. Rates
// already incorporate the native interval, resource nutrition and growth speed.
type AnimalProduct struct {
	Pawn, Race                            string
	Active, Reachable                     domain.Fact[bool]
	NutritionPerDay, WorkPerDay, LeadDays domain.Fact[float64]
}

// AnimalProductChannels aggregates independent comps by race. A missing rate
// makes that race unknown rather than silently claiming a partial herd yield.
func AnimalProductChannels(animals []AnimalProduct) []FoodChannel {
	type aggregate struct {
		nutrition, work, lead float64
		unknown               bool
		pawns                 map[string]bool
	}
	byRace := map[string]*aggregate{}
	for _, a := range animals {
		active, ak := a.Active.Value()
		if ak && !active {
			continue
		}
		r := byRace[a.Race]
		if r == nil {
			r = &aggregate{lead: math.Inf(1), pawns: map[string]bool{}}
			byRace[a.Race] = r
		}
		n, nk := a.NutritionPerDay.Value()
		w, wk := a.WorkPerDay.Value()
		l, lk := a.LeadDays.Value()
		reachable, rk := a.Reachable.Value()
		if !foodID(a.Pawn) || !foodID(a.Race) || nk && !foodNumber(n) || wk && !foodNumber(w) || lk && !foodNumber(l) {
			r.nutrition = math.NaN()
		}
		if !ak || !nk || !wk || !lk || !rk || !reachable {
			r.unknown = true
			continue
		}
		r.nutrition += n
		r.work += w
		r.lead = math.Min(r.lead, l)
		r.pawns[a.Pawn] = true
	}
	keys := make([]string, 0, len(byRace))
	for race := range byRace {
		keys = append(keys, race)
	}
	sort.Strings(keys)
	var channels []FoodChannel
	for _, race := range keys {
		r := byRace[race]
		c := FoodChannel{Kind: FoodAnimalProduct, ID: race, Terms: []FoodPlanTerm{{Name: "productive_animals", Value: float64(len(r.pawns))}}}
		if !r.unknown || math.IsNaN(r.nutrition) {
			c.NutritionPerDay = domain.Known(r.nutrition)
		}
		if !r.unknown {
			c.WorkPerDay = domain.Known(r.work)
			c.LeadDays = domain.Known(r.lead)
			c.Open = domain.Known(true)
		}
		channels = append(channels, c)
	}
	return channels
}

// FoodHerdPolicy protects admitted productive animals when their nutrition per
// work exceeds the marginal crop channel. Operator floors are never lowered.
// An operator ceiling bounds a derived floor, but cannot erase an explicit floor.
func FoodHerdPolicy(herd HerdPolicy, fact domain.Fact[FoodPlan]) HerdPolicy {
	plan, known := fact.Value()
	if !known {
		return herd
	}
	minimum := map[Resource]int64{}
	for race, n := range herd.PopulationMin {
		minimum[race] = n
	}
	marginalCrop := math.Inf(1)
	for _, e := range plan.Portfolio {
		n, nk := e.Channel.NutritionPerDay.Value()
		w, wk := e.Channel.WorkPerDay.Value()
		if e.Channel.Kind == FoodCrop && nk && wk && n > 0 && w > 0 {
			marginalCrop = math.Min(marginalCrop, n/w)
		}
	}
	if math.IsInf(marginalCrop, 1) {
		marginalCrop = 0
	}
	for _, e := range plan.Portfolio {
		if e.Channel.Kind != FoodAnimalProduct || e.Decision == FoodPlanClose || e.DeliveredPerDay <= 0 {
			continue
		}
		n, nk := e.Channel.NutritionPerDay.Value()
		w, wk := e.Channel.WorkPerDay.Value()
		if !nk || !wk || n <= 0 || w > 0 && n/w <= marginalCrop {
			continue
		}
		for _, term := range e.Channel.Terms {
			if term.Name != "productive_animals" || !foodNumber(term.Value) || term.Value > 256 {
				continue
			}
			floor := int64(math.Ceil(term.Value))
			race := Resource(e.Channel.ID)
			if ceiling, ok := herd.PopulationMax[race]; ok {
				floor = min(floor, ceiling)
			}
			minimum[race] = max(minimum[race], floor)
		}
	}
	herd.PopulationMin = minimum
	return herd
}
