package observation

import (
	"math"
	"testing"

	"google.golang.org/protobuf/proto"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The butcher bench's human corpse nutrition is the live MeatAmount sum times
// the Nutrition stat of the race's meat def (native used to multiply it).
func TestHumanCorpseNutritionPricesMeatOffTheRaceRow(t *testing.T) {
	catalog := catalogOf(t, "Human", "Meat_Human")
	got, ok := humanCorpseNutrition([]*o.HumanCorpseMeat{{Race: proto.String("Human"), MeatAmount: proto.Float64(100)}}, catalog).Value()
	if !ok || math.Abs(got-100*0.05) > 1e-6 {
		t.Fatalf("100 meat of a human is 5 nutrition, got %v %v", got, ok)
	}
	if got, ok := humanCorpseNutrition(nil, catalog).Value(); !ok || got != 0 {
		t.Fatalf("no corpses is no nutrition, got %v %v", got, ok)
	}
	if _, ok := humanCorpseNutrition([]*o.HumanCorpseMeat{{Race: proto.String("NoSuchRace"), MeatAmount: proto.Float64(1)}}, catalog).Value(); ok {
		t.Fatal("an unknown race priced")
	}
}
