package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Animal feed runway. Each race group of the kept herd
// eats NutritionPerDay (the food forecast's per-animal rate); the pasture of
// the pens feeds part of it and the unheld stock every animal of the group can
// eat the rest. The runway is that stock over the net daily consumption,
// against ProjectionHorizonDays. The shortfall is a forward-projector domain
// and the feed to stock ahead of it is a MaintainResource level in the
// Rounder's construction memory, like FuelRunway. Unknown consumption, pasture,
// stock or feed leaves the projection unknown, never defaulted.
//
// Herd growth from known events is in it: a pregnant animal adds
// the catalog's mean litter, eating as a newborn from the due tick and stepping
// up each life stage after it, and a young animal steps up at its next life
// stage tick, both by the stages' hunger rate factors. A new conception stays
// reactive, and the litter is an estimate the census corrects once born. The
// pasture credit stays the present herd's: growth is fed from the stock.

// AnimalFeedInputs are what the animal feed runway is computed from.
type AnimalFeedInputs struct {
	Animals  domain.Fact[[]UpkeepAnimal]
	Directed []Resource
	Food     domain.Fact[FoodSupply]
	Forecast domain.Fact[FoodForecast]
	// Pens are the pens' worst-season pasture rates (PenGrazing).
	Pens  domain.Fact[[]PenGrazing]
	Races AnimalRaceCatalog
	Stock StockReader
}

// AnimalFeedGroupRunway is one race group's runway. NutritionPerDay is the
// group's consumption, PasturePerDay the share of the pens' pasture it is
// credited (in proportion to its consumption), StockNutrition the unheld
// edible stock all its animals can eat. Feed is the item demanded when the
// group is short ("" when it is not, or no item feeds the race), Need the
// level of it to hold and Short the items missing from the stock of it.
type AnimalFeedGroupRunway struct {
	Definition                                     Resource
	Animals                                        []PawnID
	NutritionPerDay, PasturePerDay, StockNutrition float64
	// Steps are the known changes of the group's consumption within the
	// horizon: births and life stages.
	Steps         []FeedStep
	ShortfallDays float64
	Feed          Resource
	Need, Short   int64
}

// FeedStep is a change of a group's consumption: from Tick (game ticks from
// now) the group eats Delta more nutrition per day.
type FeedStep struct {
	Tick  int64
	Delta float64
}

// growthSteps are the consumption steps a pregnancy and the next life stages
// of one animal eating need a day make. Not known when the race, a
// stage, the litter or the event ticks are not.
func growthSteps(a UpkeepAnimal, need float64, races AnimalRaceCatalog) ([]FeedStep, bool) {
	race, in := races.Race(a.Definition)
	stages := race.LifeStages
	idx, known := a.Herd.LifeStageIndex.Value()
	if !in || !known || idx < 0 || int(idx) >= len(stages) {
		return nil, false
	}
	var steps []FeedStep
	if next := int(idx) + 1; next < len(stages) {
		ticks, tk := a.Herd.TicksToNextLifeStage.Value()
		current := stages[idx].HungerRateFactor
		if !tk || !(current > 0) {
			return nil, false
		}
		prev := need
		for k := next; k < len(stages); k++ {
			rate := need * stages[k].HungerRateFactor / current
			steps = append(steps, FeedStep{ticks + stages[k].MinAgeTicks - stages[next].MinAgeTicks, rate - prev})
			prev = rate
		}
	}
	pregnant, pk := a.SlaughterFacts.Pregnant.Value()
	if !pk {
		return nil, false
	}
	if !pregnant {
		return steps, true
	}
	birth, bk := a.Herd.TicksToBirth.Value()
	litter, lk := race.LitterSize.Value()
	adult, ak := race.AdultFeedPerDay.Value()
	last := stages[len(stages)-1].HungerRateFactor
	if !bk || !lk || !ak || !(last > 0) {
		return nil, false
	}
	prev := 0.0
	for k, s := range stages {
		rate := litter * adult * s.HungerRateFactor / last
		tick := birth
		if k > 0 {
			tick += s.MinAgeTicks
		}
		steps = append(steps, FeedStep{tick, rate - prev})
		prev = rate
	}
	return steps, true
}

// runwayOver is how the stock lasts against a group eating base a day, less
// pasture, with steps changing the rate (all per day, steps in ticks): the
// nutrition missing over the horizon and the days the stock lasts, the
// horizon when it lasts.
func runwayOver(base, pasture, stock float64, steps []FeedStep) (missing, runwayDays float64) {
	sort.SliceStable(steps, func(i, j int) bool { return steps[i].Tick < steps[j].Tick })
	horizon := ProjectionHorizonDays
	at, rate, eaten := 0.0, base, 0.0
	runwayDays = horizon
	exhausted := false
	advance := func(to float64) {
		net := math.Max(0, rate-pasture)
		if !exhausted && net > 0 && eaten+net*(to-at) > stock {
			runwayDays, exhausted = at+(stock-eaten)/net, true
		}
		eaten += net * (to - at)
		at = to
	}
	for _, s := range steps {
		day := float64(s.Tick) / domain.TicksPerDay
		if day >= horizon {
			break
		}
		advance(math.Max(day, at))
		rate += s.Delta
	}
	advance(horizon)
	return eaten - stock, runwayDays
}

// AnimalFeedProjection is the animal feed domain of the forward projection.
// ShortfallDays is the largest group shortfall. Gaps lists the race groups
// that are short and that no item feeds.
type AnimalFeedProjection struct {
	Groups        []AnimalFeedGroupRunway
	ShortfallDays float64
	Gaps          []Resource
}

// Short reports a group the stock and pasture do not carry over the horizon.
func (p AnimalFeedProjection) Short() bool { return p.ShortfallDays > 0 }

// AnimalFeedRunway is the review's animal feed runway: Needs the stock levels
// MaintainResource must reach, the projection beside them.
type AnimalFeedRunway struct {
	Needs      map[Resource]int64
	Projection domain.Fact[AnimalFeedProjection]
}

func unknownFeedRunway() AnimalFeedRunway {
	return AnimalFeedRunway{Projection: domain.Unknown[AnimalFeedProjection]()}
}

// feedEligible are the animals the herd feed covers: not marked for release or
// slaughter and not of a directed herd. Unknown when an animal's removal flags
// are.
func feedEligible(animals []UpkeepAnimal, directed []Resource) ([]UpkeepAnimal, bool) {
	skip := map[Resource]bool{}
	for _, race := range directed {
		skip[race] = true
	}
	var out []UpkeepAnimal
	for _, a := range animals {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return nil, false
		}
		if !release && !slaughter && !skip[a.Definition] {
			out = append(out, a)
		}
	}
	return out, true
}

// feedItem is the item a short group is fed with: the lowest (defName, id)
// stock every animal of it can eat, else the cheapest item a recipe makes that
// its race can eat. known is false for a race the catalog lacks; ok false when
// nothing feeds it.
func feedItem(g *AnimalFeedGroupRunway, stocks []FoodStock, races AnimalRaceCatalog) (item Resource, perItem float64, ok, known bool) {
	var bestID string
	found := false
	for _, s := range stocks {
		if !groupEats(s, g.Animals) {
			continue
		}
		count, _ := s.Count.Value()
		nutrition, _ := s.Nutrition.Value()
		if !found || s.DefName < item || (s.DefName == item && s.ID < bestID) {
			item, bestID, perItem, found = s.DefName, s.ID, nutrition/float64(count), true
		}
	}
	if found {
		return item, perItem, true, true
	}
	race, in := races.Race(g.Definition)
	if !in {
		return "", 0, false, false
	}
	for _, f := range race.FeedItems {
		if !validResource(f.Def) || !foodNumber(f.Nutrition) || f.Nutrition <= 0 {
			return "", 0, false, false
		}
		if !found || f.Nutrition < perItem || (f.Nutrition == perItem && f.Def < item) {
			item, perItem, found = f.Def, f.Nutrition, true
		}
	}
	return item, perItem, found, true
}

// PlanAnimalFeedRunway rolls every race group of the herd forward over
// ProjectionHorizonDays: the group eats its animals' NutritionPerDay less its
// share of the pens' pasture, the stock every animal of it can eat covers the
// rest for a runway of days, and a shorter runway demands the items of the
// feed (the stock's, else the cheapest producible) the horizon's missing
// nutrition takes, as an absolute stock level counted only where the stock
// falls short. A short group no item feeds is listed in Gaps.
func PlanAnimalFeedRunway(in AnimalFeedInputs) AnimalFeedRunway {
	animals, known := in.Animals.Value()
	if !known {
		return unknownFeedRunway()
	}
	eligible, known := feedEligible(animals, in.Directed)
	if !known {
		return unknownFeedRunway()
	}
	if len(eligible) == 0 {
		return AnimalFeedRunway{Projection: domain.Known(AnimalFeedProjection{})}
	}
	supply, known := in.Food.Value()
	pens, pk := in.Pens.Value()
	if !known || !pk {
		return unknownFeedRunway()
	}
	forecast, reviewed := in.Forecast.Value()
	if !reviewed {
		ids := make([]PawnID, len(eligible))
		for i, a := range eligible {
			ids[i] = a.ID
		}
		var err error
		if forecast, err = ForecastFood(supply, ids); err != nil {
			return unknownFeedRunway()
		}
	}
	perDay := map[PawnID]float64{}
	for _, row := range forecast.Consumers {
		perDay[row.ID] = row.NutritionPerDay
	}
	pasture := 0.0
	for _, p := range pens {
		d, dk := p.DemandPerDay.Value()
		y, yk := p.PasturePerDay.Value()
		if !dk || !yk || !foodNumber(d) || !foodNumber(y) {
			return unknownFeedRunway()
		}
		pasture += math.Min(d, y)
	}
	byRace := map[Resource]*AnimalFeedGroupRunway{}
	total := 0.0
	for _, a := range eligible {
		need, exists := perDay[a.ID]
		if !exists || !foodNumber(need) {
			return unknownFeedRunway()
		}
		g := byRace[a.Definition]
		if g == nil {
			g = &AnimalFeedGroupRunway{Definition: a.Definition}
			byRace[a.Definition] = g
		}
		steps, known := growthSteps(a, need, in.Races)
		if !known {
			return unknownFeedRunway()
		}
		g.Animals = append(g.Animals, a.ID)
		g.Steps = append(g.Steps, steps...)
		g.NutritionPerDay += need
		total += need
	}
	out := AnimalFeedRunway{}
	projection := AnimalFeedProjection{}
	for _, g := range byRace {
		sort.Slice(g.Animals, func(i, j int) bool { return g.Animals[i] < g.Animals[j] })
		for _, stock := range supply.Stocks {
			if groupEats(stock, g.Animals) {
				nutrition, _ := stock.Nutrition.Value()
				g.StockNutrition += nutrition
			}
		}
		if total > 0 {
			g.PasturePerDay = math.Min(g.NutritionPerDay, pasture*g.NutritionPerDay/total)
		}
		if !foodNumber(g.StockNutrition) || !foodNumber(g.NutritionPerDay) {
			return unknownFeedRunway()
		}
		missing, runway := runwayOver(g.NutritionPerDay, g.PasturePerDay, g.StockNutrition, g.Steps)
		if missing > 0 {
			g.ShortfallDays = ProjectionHorizonDays - runway
			item, perItem, ok, known := feedItem(g, supply.Stocks, in.Races)
			if !known {
				return unknownFeedRunway()
			}
			if !ok {
				projection.Gaps = append(projection.Gaps, g.Definition)
			} else {
				have, hk := in.Stock.Count(item).Value()
				if !hk {
					return unknownFeedRunway()
				}
				g.Feed, g.Short = item, int64(math.Ceil(missing/perItem))
				g.Need = have + g.Short
				if out.Needs == nil {
					out.Needs = map[Resource]int64{}
				}
				out.Needs[item] = max(out.Needs[item], g.Need)
			}
		}
		projection.ShortfallDays = math.Max(projection.ShortfallDays, g.ShortfallDays)
		projection.Groups = append(projection.Groups, *g)
	}
	sort.Slice(projection.Groups, func(i, j int) bool { return projection.Groups[i].Definition < projection.Groups[j].Definition })
	sort.Slice(projection.Gaps, func(i, j int) bool { return projection.Gaps[i] < projection.Gaps[j] })
	out.Projection = domain.Known(projection)
	return out
}

// AnimalFeedRunway is PlanAnimalFeedRunway of the review's facts: the herd's
// food forecast is the food plan's when it is read.
func (f RoundsFacts) AnimalFeedRunway() AnimalFeedRunway {
	return PlanAnimalFeedRunway(f.animalFeedInputs())
}

func (f RoundsFacts) animalFeedInputs() AnimalFeedInputs {
	u := f.AnimalUpkeep
	in := AnimalFeedInputs{Animals: u.Animals, Directed: u.DirectedHerds, Food: u.Food, Forecast: u.Forecast, Pens: f.PenGrazing, Races: u.AnimalRaces,
		Stock: StockReader{Resources: f.Resources, Wood: f.Wood}}
	if plan, known := f.FoodPlan.Value(); known {
		in.Forecast = domain.Known(plan.Forecast)
	}
	return in
}

// groupEats reports unheld stock with positive known count and nutrition
// that every one of the animals can eat.
func groupEats(s FoodStock, animals []PawnID) bool {
	holder, hk := s.Holder.Value()
	if !hk || holder != "" || s.DefName == "" || !validResource(s.DefName) {
		return false
	}
	count, ck := s.Count.Value()
	nutrition, nk := s.Nutrition.Value()
	if !ck || count <= 0 || !nk || !foodNumber(nutrition) || nutrition <= 0 {
		return false
	}
	eaters := map[PawnID]bool{}
	for _, e := range s.Eaters {
		eaters[e] = true
	}
	for _, id := range animals {
		if !eaters[id] {
			return false
		}
	}
	return true
}
