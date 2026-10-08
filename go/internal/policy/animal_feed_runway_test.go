package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func feedRunwayInputs(stockNutrition float64, pens []PenGrazing, held int64) AnimalFeedInputs {
	v := animalFixture(stockNutrition)
	return AnimalFeedInputs{
		Animals: v.Animals,
		Food:    v.Food,
		Pens:    domain.Known(pens),
		Races:   AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": {Def: "Muffalo", FeedItems: []RaceFeedItem{{Def: "Kibble", Nutrition: 0.05}}}}},
		Stock:   StockReader{Resources: domain.Known([]Amount{{Resource: "Hay", Count: held}})},
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
	in.Races = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": {Def: "Muffalo"}}}
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
	if d, reason := shadowShortfall(ForwardProjection{}, ShadowAnimalFeed); d != 0 || reason == "" {
		t.Fatal(d, reason)
	}
}

func TestShadowShortfallAnimalFeed(t *testing.T) {
	p := ForwardProjection{AnimalFeed: domain.Known(AnimalFeedProjection{ShortfallDays: 2})}
	if d, reason := shadowShortfall(p, ShadowAnimalFeed); d != 2 || reason != "" {
		t.Fatal(d, reason)
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
