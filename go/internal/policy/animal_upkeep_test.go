package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func upkeepAnimal(id PawnID, def Resource) UpkeepAnimal {
	return UpkeepAnimal{ID: id, Definition: def, RequiresPen: domain.Known(true), Contained: domain.Known(false), Release: domain.Known(false), Bonded: domain.Known(false), Slaughter: domain.Known(false)}
}

// animalFixture is one muffalo eating 1 nutrition a day beside a colonist,
// with nutrition of shared stock either can eat (5 days of reserve is 5).
func animalFixture(nutrition float64) AnimalUpkeepObservation {
	return AnimalUpkeepObservation{
		Animals: domain.Known([]UpkeepAnimal{upkeepAnimal("muffalo", "Muffalo")}),
		Food:    domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "muffalo", NutritionPerDay: domain.Known(1.0)}, {ID: "human", NutritionPerDay: domain.Known(1.0)}}, Stocks: []FoodStock{{ID: "shared", DefName: "Hay", Holder: domain.Known(PawnID("")), Count: domain.Known(int64(10)), Nutrition: domain.Known(nutrition), Eaters: []PawnID{"human", "muffalo"}, Perishable: domain.Known(false)}}}),
	}
}

const testReserveDays = DefaultFoodReserveDays

func TestAnimalFeedReserveTopsUpToFiveDaysWithoutHysteresis(t *testing.T) {
	for _, step := range []struct {
		stock   float64
		deficit float64
	}{{0, 5}, {3, 2}, {4.9, 0.1}, {5, 0}, {9, 0}, {4.9, 0.1}} {
		r, err := ReviewAnimalUpkeep(animalFixture(step.stock), AnimalUpkeepHistory{}, testReserveDays)
		rows, known := r.Feed.Value()
		if err != nil || !known {
			t.Fatalf("stock %v: %+v %v", step.stock, r, err)
		}
		if step.deficit == 0 {
			if len(rows) != 0 {
				t.Fatalf("stock %v: reserve met but planned %+v", step.stock, rows)
			}
			continue
		}
		if len(rows) != 1 || rows[0].Definition != "Muffalo" || rows[0].TargetNutrition != 5 || math.Abs(rows[0].DeficitNutrition-step.deficit) > 1e-9 || !reflect.DeepEqual(rows[0].Animals, []PawnID{"muffalo"}) {
			t.Fatalf("stock %v: %+v", step.stock, rows)
		}
	}
}

// A colony with animals and no hunger yet still plans the reserve.
func TestAnimalFeedReservePlannedBeforeAnyHunger(t *testing.T) {
	v := animalFixture(0)
	food, _ := v.Food.Value()
	food.Stocks = nil
	v.Food = domain.Known(food)
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 1 || rows[0].DeficitNutrition != 5 {
		t.Fatalf("%+v", rows)
	}
}

func TestAnimalFeedReserveIgnoresHeldAndUneatenStock(t *testing.T) {
	v := animalFixture(0)
	food, _ := v.Food.Value()
	food.Stocks = []FoodStock{
		{ID: "held", DefName: "Hay", Holder: domain.Known(PawnID("human")), Count: domain.Known(int64(10)), Nutrition: domain.Known(100.0), Eaters: []PawnID{"human"}, Perishable: domain.Known(false)},
		{ID: "other", DefName: "Kibble", Holder: domain.Known(PawnID("")), Count: domain.Known(int64(10)), Nutrition: domain.Known(100.0), Eaters: []PawnID{"human"}, Perishable: domain.Known(false)},
	}
	v.Food = domain.Known(food)
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 1 || rows[0].StockNutrition != 0 || rows[0].DeficitNutrition != 5 {
		t.Fatalf("%+v", rows)
	}
}

func TestAnimalFeedReserveGroupsByRaceAndIntersectsReach(t *testing.T) {
	m1, m2, h1 := upkeepAnimal("m1", "Muffalo"), upkeepAnimal("m2", "Muffalo"), upkeepAnimal("h1", "Husky")
	m1.ReachableBenches = []string{"Thing_ButcherSpot2", "Thing_ButcherSpot1", "Thing_ButcherSpot3"}
	m2.ReachableBenches = []string{"Thing_ButcherSpot3", "Thing_ButcherSpot1"}
	h1.ReachableBenches = []string{"Thing_ButcherSpot9"}
	kibble := []AnimalFeedStorage{{Zone: "Zone_3", Accepts: []string{"Kibble"}}}
	m1.ReachableStorage, m2.ReachableStorage = kibble, kibble
	m1.StorageCandidates = []domain.Cell{{X: 5, Z: 5}, {X: 6, Z: 5}, {X: 9, Z: 9}}
	m2.StorageCandidates = []domain.Cell{{X: 6, Z: 5}, {X: 9, Z: 9}}
	v := AnimalUpkeepObservation{
		Animals: domain.Known([]UpkeepAnimal{h1, m2, m1}),
		Food: domain.Known(FoodSupply{Complete: domain.Known(true),
			Consumers: []FoodConsumer{{ID: "human", NutritionPerDay: domain.Known(1.0)}, {ID: "m1", NutritionPerDay: domain.Known(1.0)}, {ID: "m2", NutritionPerDay: domain.Known(2.0)}, {ID: "h1", NutritionPerDay: domain.Known(0.5)}},
			// Hay feeds both races: it counts toward each (accepted double count).
			Stocks: []FoodStock{{ID: "hay", DefName: "Hay", Holder: domain.Known(PawnID("")), Count: domain.Known(int64(10)), Nutrition: domain.Known(2.0), Eaters: []PawnID{"m1", "m2", "h1"}, Perishable: domain.Known(false)}}}),
	}
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 2 || rows[0].Definition != "Muffalo" || rows[1].Definition != "Husky" {
		t.Fatalf("%+v", rows)
	}
	muffalo, husky := rows[0], rows[1]
	if muffalo.TargetNutrition != 15 || muffalo.DeficitNutrition != 13 || !reflect.DeepEqual(muffalo.Animals, []PawnID{"m1", "m2"}) {
		t.Fatalf("%+v", muffalo)
	}
	if husky.TargetNutrition != 2.5 || husky.DeficitNutrition != 0.5 {
		t.Fatalf("%+v", husky)
	}
	if !reflect.DeepEqual(muffalo.ReachableBenches, []string{"Thing_ButcherSpot1", "Thing_ButcherSpot3"}) || !reflect.DeepEqual(husky.ReachableBenches, []string{"Thing_ButcherSpot9"}) {
		t.Fatalf("benches %v %v", muffalo.ReachableBenches, husky.ReachableBenches)
	}
	if len(muffalo.ReachableStorage) != 2 || !reflect.DeepEqual(muffalo.StorageCandidates, []domain.Cell{{X: 6, Z: 5}}) {
		t.Fatalf("%+v", muffalo)
	}
}

func TestAnimalIndependentUnknownsAndRemoval(t *testing.T) {
	previous := AnimalUpkeepHistory{Containment: true}
	r, err := ReviewAnimalUpkeep(AnimalUpkeepObservation{}, previous, testReserveDays)
	if err != nil || !reflect.DeepEqual(r.History, previous) {
		t.Fatal(r, err)
	}
	if _, known := r.Feed.Value(); known {
		t.Fatal("missing census recovered feed")
	}
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	animals[0].Contained = domain.Unknown[bool]()
	v.Animals = domain.Known(animals)
	r, err = ReviewAnimalUpkeep(v, previous, testReserveDays)
	if _, known := r.Containment.Value(); known || err != nil {
		t.Fatal(r, err)
	}
	if rows, known := r.Feed.Value(); !known || len(rows) != 1 {
		t.Fatal(r)
	}
	animals[0].RequiresPen = domain.Known(false)
	v.Animals = domain.Known(animals)
	v.Food = domain.Unknown[FoodSupply]()
	r, err = ReviewAnimalUpkeep(v, previous, testReserveDays)
	if rows, known := r.Containment.Value(); !known || len(rows) != 0 || err != nil {
		t.Fatal(r, err)
	}
	if _, known := r.Feed.Value(); known {
		t.Fatal("unknown food recovered feed")
	}
	v.Animals = domain.Known([]UpkeepAnimal{})
	r, err = ReviewAnimalUpkeep(v, previous, testReserveDays)
	if rows, known := r.Feed.Value(); !known || len(rows) != 0 || err != nil {
		t.Fatal(r, err)
	}
}

// An unread nutrition fact adds nothing: the animal has no forecast row.
func TestAnimalFeedReserveUnknownNutritionAddsNothing(t *testing.T) {
	v := animalFixture(0)
	food, _ := v.Food.Value()
	food.Consumers = []FoodConsumer{{ID: "muffalo", NutritionPerDay: domain.Unknown[float64]()}}
	v.Food = domain.Known(food)
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays)
	if rows, known := r.Feed.Value(); err == nil && known && len(rows) != 0 {
		t.Fatalf("unread nutrition planned %+v", rows)
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
		r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays)
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
	if _, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays); err == nil {
		t.Fatal("duplicate accepted")
	}
	if _, err := ReviewAnimalUpkeep(animalFixture(0), AnimalUpkeepHistory{}, math.NaN()); err == nil {
		t.Fatal("NaN accepted")
	}
}

func TestAnimalFeedGroupCarriesReachableBenches(t *testing.T) {
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	animals[0].ReachableBenches = []string{"Thing_ButcherSpot7"}
	v.Animals = domain.Known(animals)
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 1 || !reflect.DeepEqual(rows[0].ReachableBenches, []string{"Thing_ButcherSpot7"}) {
		t.Fatal(rows)
	}
	animals[0].ReachableBenches = []string{""}
	v.Animals = domain.Known(animals)
	if _, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays); err == nil {
		t.Fatal("blank bench id accepted")
	}
}

func TestAnimalFeedGroupCarriesReachableStorage(t *testing.T) {
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	storage := []AnimalFeedStorage{{Zone: "Zone_2", Accepts: []string{"Kibble"}}}
	cells := []domain.Cell{{X: 3, Z: 4}}
	animals[0].ReachableStorage, animals[0].StorageCandidates = storage, cells
	v.Animals = domain.Known(animals)
	rows := mustFeed(t, mustReview(t, v))
	if len(rows) != 1 || !reflect.DeepEqual(rows[0].ReachableStorage, [][]AnimalFeedStorage{storage}) || !reflect.DeepEqual(rows[0].StorageCandidates, cells) {
		t.Fatal(rows)
	}
	animals[0].ReachableStorage = []AnimalFeedStorage{{Zone: "Zone_2", Accepts: []string{""}}}
	v.Animals = domain.Known(animals)
	if _, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays); err == nil {
		t.Fatal("blank accepted definition accepted")
	}
}

func mustReview(t *testing.T, v AnimalUpkeepObservation) AnimalUpkeepReview {
	r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, testReserveDays)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func mustFeed(t *testing.T, r AnimalUpkeepReview) []AnimalFeedGroup {
	rows, known := r.Feed.Value()
	if !known {
		t.Fatal("feed unknown")
	}
	return rows
}
