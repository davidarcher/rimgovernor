package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestFoodChannelRoundTripsThroughSupplyCandidate(t *testing.T) {
	for _, c := range []FoodChannel{
		{Kind: FoodHunt, ID: "deer", NutritionPerDay: domain.Known(2.0), WorkPerDay: domain.Known(7500.0), LeadDays: domain.Known(0.0), Open: domain.Known(false),
			Risk: []FoodRisk{{FoodRevenge, 0.3}}, Terms: []FoodPlanTerm{{"revenge_chance", 0.1}}, Prey: []string{"a", "b"}},
		{Kind: FoodSlaughter, ID: "slaughter:cow", StockCap: domain.Known(int64(15)), WorkPerDay: domain.Known(0.0), UpfrontTicks: domain.Known(180.0), LeadDays: domain.Known(0.0), Open: domain.Known(false)},
		{Kind: FoodFishing, ID: "pond", NutritionPerDay: domain.Known(1.0), WorkPerDay: domain.Known(1.0), LeadDays: domain.Known(2.0), Open: domain.Known(true), DistanceSquared: domain.Known(9.0)},
		{Kind: FoodCrop, ID: "field", NutritionPerDay: domain.Unknown[float64](), WorkPerDay: domain.Known(1.0), LeadDays: domain.Known(5.0)},
	} {
		got, ok := FoodChannelOfSupply(SupplyCandidateOfFood(c))
		if !ok || !reflect.DeepEqual(normalizeFood(got), normalizeFood(c)) {
			t.Fatalf("%s/%s: got %+v ok=%v, want %+v", c.Kind, c.ID, got, ok, c)
		}
	}
	for kind := range foodCandidateKinds {
		if _, ok := foodKindOf(foodCandidateKinds[kind]); !ok {
			t.Fatal(kind)
		}
	}
}

func TestSupplyCandidateOfFoodIsOneNutritionYield(t *testing.T) {
	c := SupplyCandidateOfFood(FoodChannel{Kind: FoodForage, ID: "berries", NutritionPerDay: domain.Known(1.5), Open: domain.Known(true)})
	state, _ := c.State.Value()
	rate, _ := c.Yields[0].PerDay.Value()
	if c.Kind != CandidateForage || state != CandidateDelivering || len(c.Yields) != 1 || c.Yields[0].Good.Def != CandidateNutrition || rate != 1.5 {
		t.Fatalf("%+v", c)
	}
}

// normalizeFood maps empty slices to nil, which the adapters do not preserve
// and no consumer distinguishes.
func normalizeFood(c FoodChannel) FoodChannel {
	if len(c.Prey) == 0 {
		c.Prey = nil
	}
	if len(c.Risk) == 0 {
		c.Risk = nil
	}
	if len(c.Terms) == 0 {
		c.Terms = nil
	}
	return c
}
