package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// muffaloRace is a race of two life stages, a young one eating half the adult
// feed until four days of age, the adult eating 1 a day, and a mean litter of 2.
func muffaloRace(feed ...RaceFeedItem) AnimalRace {
	return AnimalRace{Def: "Muffalo", FeedItems: feed, AdultFeedPerDay: domain.Known(1.0), LitterSize: domain.Known(2.0),
		LifeStages: []RaceLifeStage{{MinAgeTicks: 0, HungerRateFactor: 0.5}, {MinAgeTicks: 4 * domain.TicksPerDay, HungerRateFactor: 1}}}
}

// feedRunwayInputs is one adult, not pregnant muffalo eating 1 a day.
func feedRunwayInputs(stockNutrition float64, pens []PenGrazing, held int64) AnimalFeedInputs {
	v := animalFixture(stockNutrition)
	animals, _ := v.Animals.Value()
	animals[0].SlaughterFacts.Pregnant = domain.Known(false)
	animals[0].Herd.LifeStageIndex = domain.Known(int32(1))
	return AnimalFeedInputs{
		Animals: domain.Known(animals),
		Food:    v.Food,
		Pens:    domain.Known(pens),
		Races:   AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": muffaloRace(RaceFeedItem{Def: "Kibble", Nutrition: 0.05})}},
		Stock:   StockReader{Resources: domain.Known([]Amount{{Resource: "Hay", Count: held}})},
	}
}

func withMuffalo(in AnimalFeedInputs, edit func(*UpkeepAnimal)) AnimalFeedInputs {
	animals, _ := in.Animals.Value()
	animals = append([]UpkeepAnimal{}, animals...)
	edit(&animals[0])
	in.Animals = domain.Known(animals)
	return in
}

// A pregnancy adds the mean litter eating as newborns from the due tick: due in
// 2 days, 2 newborns at half the adult feed eat 1 a day more for the 3 days left.
func TestAnimalFeedRunwayPregnancyAddsNewborns(t *testing.T) {
	due := func(a *UpkeepAnimal) {
		a.SlaughterFacts.Pregnant = domain.Known(true)
		a.Herd.TicksToBirth = domain.Known(int64(2 * domain.TicksPerDay))
	}
	r := PlanAnimalFeedRunway(withMuffalo(feedRunwayInputs(6, []PenGrazing{pen(0, 0)}, 10), due))
	p, known := r.Projection.Value()
	// 2 eaten by day 2, then 2 a day: the 6 held last until day 4.
	if !known || math.Abs(p.ShortfallDays-1) > 1e-9 || len(p.Groups[0].Steps) != 2 {
		t.Fatalf("%+v", r)
	}
	// 8 needed over the horizon, 6 held: 2 missing, 0.6 per hay is 4 more than the 10 held.
	if r.Needs["Hay"] != 14 {
		t.Fatalf("%+v", r)
	}
	// The same herd without the pregnancy is covered.
	if p, _ := PlanAnimalFeedRunway(feedRunwayInputs(6, []PenGrazing{pen(0, 0)}, 10)).Projection.Value(); p.Short() {
		t.Fatalf("%+v", p)
	}
	// A birth beyond the horizon changes nothing in the projection.
	late := PlanAnimalFeedRunway(withMuffalo(feedRunwayInputs(6, []PenGrazing{pen(0, 0)}, 10), func(a *UpkeepAnimal) {
		due(a)
		a.Herd.TicksToBirth = domain.Known(int64(9 * domain.TicksPerDay))
	}))
	if p, _ := late.Projection.Value(); p.Short() {
		t.Fatalf("%+v", p)
	}
}

// A young animal eats the next life stage's rate from the next stage tick.
func TestAnimalFeedRunwayJuvenileMatures(t *testing.T) {
	young := func(a *UpkeepAnimal) {
		a.Herd.LifeStageIndex = domain.Known(int32(0))
		a.Herd.TicksToNextLifeStage = domain.Known(int64(domain.TicksPerDay))
	}
	// 1 on the first day, then twice that: 9 over the horizon.
	covered := PlanAnimalFeedRunway(withMuffalo(feedRunwayInputs(9, []PenGrazing{pen(0, 0)}, 10), young))
	if p, known := covered.Projection.Value(); !known || p.Short() {
		t.Fatalf("%+v", covered)
	}
	r := PlanAnimalFeedRunway(withMuffalo(feedRunwayInputs(8, []PenGrazing{pen(0, 0)}, 10), young))
	if p, _ := r.Projection.Value(); math.Abs(p.ShortfallDays-0.5) > 1e-9 {
		t.Fatalf("%+v", r)
	}
}

// An animal whose pregnancy, litter, stage or event tick is unread leaves the
// projection unknown.
func TestAnimalFeedRunwayUnknownGrowthInputs(t *testing.T) {
	pregnant := func(a *UpkeepAnimal) {
		a.SlaughterFacts.Pregnant = domain.Known(true)
		a.Herd.TicksToBirth = domain.Known(int64(domain.TicksPerDay))
	}
	base := feedRunwayInputs(100, []PenGrazing{pen(0, 0)}, 10)
	for name, edit := range map[string]func(*UpkeepAnimal){
		"pregnancy unread":     func(a *UpkeepAnimal) { a.SlaughterFacts.Pregnant = domain.Unknown[bool]() },
		"birth tick unread":    func(a *UpkeepAnimal) { pregnant(a); a.Herd.TicksToBirth = domain.Unknown[int64]() },
		"life stage unread":    func(a *UpkeepAnimal) { a.Herd.LifeStageIndex = domain.Unknown[int32]() },
		"next stage unread":    func(a *UpkeepAnimal) { a.Herd.LifeStageIndex = domain.Known(int32(0)) },
		"stage out of catalog": func(a *UpkeepAnimal) { a.Herd.LifeStageIndex = domain.Known(int32(2)) },
	} {
		if _, known := PlanAnimalFeedRunway(withMuffalo(base, edit)).Projection.Value(); known {
			t.Fatal(name, "defaulted")
		}
	}
	for name, edit := range map[string]func(*AnimalRace){
		"litter unread": func(r *AnimalRace) { r.LitterSize = domain.Unknown[float64]() },
		"adult feed":    func(r *AnimalRace) { r.AdultFeedPerDay = domain.Unknown[float64]() },
		"no stages":     func(r *AnimalRace) { r.LifeStages = nil },
	} {
		in := withMuffalo(base, pregnant)
		race := muffaloRace()
		edit(&race)
		in.Races = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": race}}
		if _, known := PlanAnimalFeedRunway(in).Projection.Value(); known {
			t.Fatal(name, "defaulted")
		}
	}
}

func pen(demand, pasture float64) PenGrazing {
	return PenGrazing{ID: "pen", DemandPerDay: domain.Known(demand), PasturePerDay: domain.Known(pasture)}
}

// A herd eating 1 a day with 2 nutrition held is 3 nutrition short over the
// 5-day horizon and demands the held stock's item for it.
func TestAnimalFeedRunwayProjectsShortfall(t *testing.T) {
	r := PlanAnimalFeedRunway(feedRunwayInputs(2, []PenGrazing{pen(0, 0)}, 10))
	p, known := r.Projection.Value()
	if !known || !p.Short() || math.Abs(p.ShortfallDays-3) > 1e-9 {
		t.Fatalf("%+v", r)
	}
	// 10 hay hold 2 nutrition: 0.2 each, 3 missing is 15 more.
	if r.Needs["Hay"] != 25 || len(p.Gaps) != 0 {
		t.Fatalf("%+v", r)
	}
	covered := PlanAnimalFeedRunway(feedRunwayInputs(5, []PenGrazing{pen(0, 0)}, 10))
	if p, _ := covered.Projection.Value(); p.Short() || len(covered.Needs) != 0 {
		t.Fatalf("%+v", covered)
	}
}

// Pasture offsets the consumption; fully grazed herds need no feed.
func TestAnimalFeedRunwayCreditsPasture(t *testing.T) {
	r := PlanAnimalFeedRunway(feedRunwayInputs(0, []PenGrazing{pen(1, 5)}, 0))
	if p, known := r.Projection.Value(); !known || p.Short() || len(r.Needs) != 0 {
		t.Fatalf("%+v", r)
	}
	r = PlanAnimalFeedRunway(feedRunwayInputs(0, []PenGrazing{pen(1, 0.5)}, 0))
	if p, _ := r.Projection.Value(); !p.Short() || p.ShortfallDays != ProjectionHorizonDays {
		t.Fatalf("%+v", r)
	}
}

// With no stock the cheapest producible feed is demanded; a race nothing
// feeds is a gap.
func TestAnimalFeedRunwayProducibleFeedAndGap(t *testing.T) {
	in := feedRunwayInputs(0, []PenGrazing{pen(0, 0)}, 0)
	in.Food = domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "muffalo", NutritionPerDay: domain.Known(1.0)}}})
	r := PlanAnimalFeedRunway(in)
	if r.Needs["Kibble"] != 100 {
		t.Fatalf("%+v", r)
	}
	in.Races = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": muffaloRace()}}
	r = PlanAnimalFeedRunway(in)
	p, known := r.Projection.Value()
	if !known || len(r.Needs) != 0 || len(p.Gaps) != 1 || p.Gaps[0] != "Muffalo" {
		t.Fatalf("%+v", r)
	}
}

// Unread pasture, animals or stock leave the projection unknown.
func TestAnimalFeedRunwayUnknownInputs(t *testing.T) {
	in := feedRunwayInputs(0, nil, 0)
	in.Pens = domain.Unknown[[]PenGrazing]()
	if _, known := PlanAnimalFeedRunway(in).Projection.Value(); known {
		t.Fatal("unknown pens defaulted")
	}
	in = feedRunwayInputs(0, []PenGrazing{pen(0, 0)}, 0)
	in.Stock = StockReader{}
	if _, known := PlanAnimalFeedRunway(in).Projection.Value(); known {
		t.Fatal("unknown stock defaulted")
	}
	in = feedRunwayInputs(0, []PenGrazing{pen(0, 0)}, 0)
	in.Animals = domain.Unknown[[]UpkeepAnimal]()
	if _, known := PlanAnimalFeedRunway(in).Projection.Value(); known {
		t.Fatal("unknown animals defaulted")
	}
}

func TestAnimalFeedFilterOf(t *testing.T) {
	races := AnimalRaceCatalog{Races: map[Resource]AnimalRace{
		"Muffalo": {Def: "Muffalo", FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: .05}}, Edible: []string{"Hay"}},
		"Husky":   {Def: "Husky", FeedItems: []RaceFeedItem{{Def: "Pemmican", Nutrition: .5}}},
	}}
	got := AnimalFeedFilterOf([]UpkeepAnimal{upkeepAnimal("a", "Muffalo"), upkeepAnimal("b", "Husky"), upkeepAnimal("c", "Ghost")}, races)
	want := []Resource{"Hay", "Kibble", "Pemmican"}
	if len(got) != 3 || got[0] != want[0] || got[1] != want[1] || got[2] != want[2] {
		t.Fatal(got)
	}
}
