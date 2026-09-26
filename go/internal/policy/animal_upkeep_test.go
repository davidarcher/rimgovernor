package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func animalFixture(nutrition float64) AnimalUpkeepObservation {
	return AnimalUpkeepObservation{
		Animals: domain.Known([]UpkeepAnimal{{ID: "muffalo", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Known(false), Release: domain.Known(false), Slaughter: domain.Known(false)}}),
		Food:    domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "muffalo", NutritionPerDay: domain.Known(1.0)}, {ID: "human", NutritionPerDay: domain.Known(1.0)}}, Stocks: []FoodStock{{ID: "shared", Holder: domain.Known(PawnID("")), Nutrition: domain.Known(nutrition), Eaters: []PawnID{"human", "muffalo"}, Perishable: domain.Known(false)}}}),
	}
}

func TestAnimalFeedCompetitionHysteresisAndExpiry(t *testing.T) {
	history := AnimalUpkeepHistory{}
	for _, step := range []struct {
		stock  float64
		active bool
	}{{4, false}, {3, true}, {6, true}, {8, false}, {6, false}, {3, true}} {
		r, err := ReviewAnimalUpkeep(animalFixture(step.stock), history, DefaultAnimalUpkeepPolicy())
		rows, known := r.Feed.Value()
		if err != nil || !known || (len(rows) > 0) != step.active {
			t.Fatalf("stock %v: %+v %v", step.stock, r, err)
		}
		if step.active && (rows[0].RunwayDays != step.stock/2 || rows[0].Nutrition != 4-step.stock/2) {
			t.Fatal(rows)
		}
		history = r.History
	}
	v := animalFixture(100)
	food, _ := v.Food.Value()
	food.Stocks[0].Perishable = domain.Known(true)
	food.Stocks[0].RotTicks = domain.Known(int64(60000))
	v.Food = domain.Known(food)
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy())
	rows, known := r.Feed.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].RunwayDays != 1 {
		t.Fatal(r, err)
	}
}

func TestAnimalIndependentUnknownsAndRemoval(t *testing.T) {
	previous := AnimalUpkeepHistory{Containment: true, Feed: []PawnID{"muffalo"}}
	r, err := ReviewAnimalUpkeep(AnimalUpkeepObservation{}, previous, DefaultAnimalUpkeepPolicy())
	if err != nil || !reflect.DeepEqual(r.History, previous) {
		t.Fatal(r, err)
	}
	if _, known := r.Feed.Value(); known {
		t.Fatal("missing census recovered feed")
	}
	v := animalFixture(6)
	animals, _ := v.Animals.Value()
	animals[0].Contained = domain.Unknown[bool]()
	v.Animals = domain.Known(animals)
	r, err = ReviewAnimalUpkeep(v, previous, DefaultAnimalUpkeepPolicy())
	if _, known := r.Containment.Value(); known || err != nil {
		t.Fatal(r, err)
	}
	if rows, known := r.Feed.Value(); !known || len(rows) != 1 {
		t.Fatal(r)
	}
	animals[0].RequiresPen = domain.Known(false)
	v.Animals = domain.Known(animals)
	v.Food = domain.Unknown[FoodSupply]()
	r, err = ReviewAnimalUpkeep(v, previous, DefaultAnimalUpkeepPolicy())
	if rows, known := r.Containment.Value(); !known || len(rows) != 0 || err != nil {
		t.Fatal(r, err)
	}
	if !reflect.DeepEqual(r.History.Feed, previous.Feed) {
		t.Fatal("unknown food lost history")
	}
	v.Animals = domain.Known([]UpkeepAnimal{})
	r, err = ReviewAnimalUpkeep(v, previous, DefaultAnimalUpkeepPolicy())
	if rows, known := r.Feed.Value(); !known || len(rows) != 0 || len(r.History.Feed) != 0 || err != nil {
		t.Fatal(r, err)
	}
}

func TestAnimalPlayerDirectionsAndInvalidInputs(t *testing.T) {
	for _, direction := range []string{"release", "slaughter", "herd"} {
		v := animalFixture(0)
		animals, _ := v.Animals.Value()
		switch direction {
		case "release":
			animals[0].Release = domain.Known(true)
		case "slaughter":
			animals[0].Slaughter = domain.Known(true)
		case "herd":
			v.DirectedHerds = []Resource{"Muffalo"}
		}
		v.Animals = domain.Known(animals)
		r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{Feed: []PawnID{"muffalo"}}, DefaultAnimalUpkeepPolicy())
		if rows, known := r.Feed.Value(); err != nil || !known || len(rows) != 0 {
			t.Fatal(direction, r, err)
		}
		rows, _ := r.Containment.Value()
		if (len(rows) > 0) != (direction == "herd") {
			t.Fatal(direction, r)
		}
	}
	v := animalFixture(0)
	animals, _ := v.Animals.Value()
	v.Animals = domain.Known(append(animals, animals[0]))
	if _, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy()); err == nil {
		t.Fatal("duplicate accepted")
	}
	p := DefaultAnimalUpkeepPolicy()
	p.FeedTargetDays = math.NaN()
	if _, err := ReviewAnimalUpkeep(animalFixture(0), AnimalUpkeepHistory{}, p); err == nil {
		t.Fatal("NaN accepted")
	}
}

func TestAnimalFeedTargetCarriesReachableBenches(t *testing.T) {
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	animals[0].ReachableBenches = []string{"Thing_ButcherSpot7"}
	v.Animals = domain.Known(animals)
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy())
	rows, known := r.Feed.Value()
	if err != nil || !known || len(rows) != 1 || !reflect.DeepEqual(rows[0].ReachableBenches, []string{"Thing_ButcherSpot7"}) {
		t.Fatal(r, err)
	}
	animals[0].ReachableBenches = []string{""}
	v.Animals = domain.Known(animals)
	if _, err = ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy()); err == nil {
		t.Fatal("blank bench id accepted")
	}
}

func TestAnimalFeedTargetCarriesReachableStorage(t *testing.T) {
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	storage := []AnimalFeedStorage{{Zone: "Zone_2", Accepts: []string{"Kibble"}}}
	cells := []domain.Cell{{X: 3, Z: 4}}
	animals[0].ReachableStorage, animals[0].StorageCandidates = storage, cells
	v.Animals = domain.Known(animals)
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy())
	rows, known := r.Feed.Value()
	if err != nil || !known || len(rows) != 1 || !reflect.DeepEqual(rows[0].ReachableStorage, storage) || !reflect.DeepEqual(rows[0].StorageCandidates, cells) {
		t.Fatal(r, err)
	}
	animals[0].ReachableStorage = []AnimalFeedStorage{{Zone: "Zone_2", Accepts: []string{""}}}
	v.Animals = domain.Known(animals)
	if _, err = ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy()); err == nil {
		t.Fatal("blank accepted definition accepted")
	}
}

// #708: a pet the gated colony forecast reports short is fed to the target
// even above the feed minimum.
func TestAnimalFeedAdmitsForecastPetShortfall(t *testing.T) {
	v := animalFixture(6) // muffalo runway 3: above the minimum 2, below target 4
	food, _ := v.Food.Value()
	forecast, err := ForecastFood(food, nil)
	if err != nil {
		t.Fatal(err)
	}
	v.Forecast = domain.Known(forecast)
	if r, _ := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy()); len(mustFeed(t, r)) != 0 {
		t.Fatal("ungated forecast admitted a pet above the minimum")
	}
	v.Forecast = domain.Known(forecast.GateOnColonists([]PawnID{"human"}, 3.5))
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 1 || rows[0].ID != "muffalo" || rows[0].Nutrition != 1 {
		t.Fatalf("shortfall not admitted: %+v", rows)
	}
}

func mustReview(t *testing.T, v AnimalUpkeepObservation) AnimalUpkeepReview {
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultAnimalUpkeepPolicy())
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func mustFeed(t *testing.T, r AnimalUpkeepReview) []AnimalFeedTarget {
	rows, known := r.Feed.Value()
	if !known {
		t.Fatal("feed unknown")
	}
	return rows
}
