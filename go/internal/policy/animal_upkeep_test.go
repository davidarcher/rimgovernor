package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func upkeepAnimal(id PawnID, def Resource) UpkeepAnimal {
	return UpkeepAnimal{ID: id, Definition: def, RequiresPen: domain.Known(true), Contained: domain.Known(false), Release: domain.Known(false), Bonded: domain.Known(false), Slaughter: domain.Known(false)}
}

// animalFixture is one muffalo eating 1 nutrition a day beside a colonist,
// with nutrition of shared stock either can eat.
func animalFixture(nutrition float64) AnimalUpkeepObservation {
	return AnimalUpkeepObservation{
		Animals: domain.Known([]UpkeepAnimal{upkeepAnimal("muffalo", "Muffalo")}),
		Food:    domain.Known(FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "muffalo", NutritionPerDay: domain.Known(1.0)}, {ID: "human", NutritionPerDay: domain.Known(1.0)}}, Stocks: []FoodStock{{ID: "shared", DefName: "Hay", Holder: domain.Known(PawnID("")), Count: domain.Known(int64(10)), Nutrition: domain.Known(nutrition), Eaters: []PawnID{"human", "muffalo"}, Perishable: domain.Known(false)}}}),
	}
}

func TestAnimalIndependentUnknownsAndRemoval(t *testing.T) {
	previous := AnimalUpkeepHistory{Containment: true}
	r, err := ReviewAnimalUpkeep(AnimalUpkeepObservation{}, previous)
	if err != nil || !reflect.DeepEqual(r.History, previous) {
		t.Fatal(r, err)
	}
	v := animalFixture(1)
	animals, _ := v.Animals.Value()
	animals[0].Contained = domain.Unknown[bool]()
	v.Animals = domain.Known(animals)
	r, err = ReviewAnimalUpkeep(v, previous)
	if _, known := r.Containment.Value(); known || err != nil {
		t.Fatal(r, err)
	}
	animals[0].RequiresPen = domain.Known(false)
	v.Animals = domain.Known(animals)
	r, err = ReviewAnimalUpkeep(v, previous)
	if rows, known := r.Containment.Value(); !known || len(rows) != 0 || err != nil {
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
		r, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{})
		if err != nil {
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
	if _, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}); err == nil {
		t.Fatal("duplicate accepted")
	}
}
