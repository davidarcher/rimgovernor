package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// AnimalProduct is one observed production comp, not harvested stock. Rates
// already incorporate the native interval, resource nutrition and growth speed.
// FeedPerDay is the animal's own daily feed, which the channel nets out.
type AnimalProduct struct {
	Pawn, Race                                        string
	Active, Reachable                                 domain.Fact[bool]
	NutritionPerDay, WorkPerDay, LeadDays, FeedPerDay domain.Fact[float64]
}

// WithFeed sets each row's FeedPerDay from the upkeep census; an animal the
// census does not list, or one with an unread feed rate, stays unknown.
func WithFeed(rows []AnimalProduct, animals domain.Fact[[]UpkeepAnimal]) []AnimalProduct {
	census, _ := animals.Value()
	feed := make(map[string]domain.Fact[float64], len(census))
	for _, a := range census {
		feed[string(a.ID)] = a.Herd.FeedPerDay
	}
	out := append([]AnimalProduct(nil), rows...)
	for i := range out {
		if f, ok := feed[out[i].Pawn]; ok {
			out[i].FeedPerDay = f
		}
	}
	return out
}

// AnimalProductChannels aggregates independent comps by race into the rate
// net of the herd's feed (product minus feed per day, never below zero). A
// missing rate or feed makes that race unknown rather than silently claiming a
// partial herd yield.
func AnimalProductChannels(animals []AnimalProduct) []SupplyCandidate {
	type aggregate struct {
		nutrition, work, lead, feed float64
		unknown                     bool
		pawns                       map[string]bool
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
		f, fk := a.FeedPerDay.Value()
		reachable, rk := a.Reachable.Value()
		if !foodID(a.Pawn) || !foodID(a.Race) || nk && !foodNumber(n) || wk && !foodNumber(w) || lk && !foodNumber(l) || fk && !foodNumber(f) {
			r.nutrition = math.NaN()
		}
		if !ak || !nk || !wk || !lk || !fk || !rk || !reachable {
			r.unknown = true
			continue
		}
		r.nutrition += n
		r.work += w
		r.feed += f
		r.lead = math.Min(r.lead, l)
		r.pawns[a.Pawn] = true
	}
	keys := make([]string, 0, len(byRace))
	for race := range byRace {
		keys = append(keys, race)
	}
	sort.Strings(keys)
	var channels []SupplyCandidate
	for _, race := range keys {
		r := byRace[race]
		c := FoodCandidate(CandidateAnimalProduct, race, domain.Unknown[float64]())
		c.Source, c.Terms = "animal_product:"+race, []CandidateTerm{{Name: "productive_animals", Value: float64(len(r.pawns))}, {Name: "feed_per_day", Value: r.feed}}
		if !r.unknown || math.IsNaN(r.nutrition) {
			c.Yields[0].PerDay = domain.Known(math.Max(0, r.nutrition-r.feed))
		}
		if !r.unknown {
			c.LaborPerDay = domain.Known(r.work)
			c.LeadDays = domain.Known(r.lead)
			c.State = FoodState(domain.Known(false), domain.Known(true))
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
		n, nk := e.Channel.Nutrition().PerDay.Value()
		w, wk := e.Channel.LaborPerDay.Value()
		if e.Channel.Kind == CandidateCrop && nk && wk && n > 0 && w > 0 {
			marginalCrop = math.Min(marginalCrop, n/w)
		}
	}
	if math.IsInf(marginalCrop, 1) {
		marginalCrop = 0
	}
	for _, e := range plan.Portfolio {
		if e.Channel.Kind != CandidateAnimalProduct || e.Decision == FoodPlanClose || !e.Selected() {
			continue
		}
		n, nk := e.Channel.Nutrition().PerDay.Value()
		w, wk := e.Channel.LaborPerDay.Value()
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
