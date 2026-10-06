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
	if _, ok := AcquisitionCandidateOfSupply(c); ok {
		t.Fatal("a rate-only food candidate is not a one-shot acquisition")
	}
}

func TestAcquisitionCandidateRoundTripsThroughSupplyCandidate(t *testing.T) {
	headroom := domain.Known(int64(40))
	for _, c := range []AcquisitionCandidate{
		{ID: "tree1", Kind: AcquisitionChop, Yields: []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: "WoodLog"}, Count: 30}, Headroom: headroom}},
			PathDistance: domain.Known(12.0), Labor: domain.Known(450.0), NeedsHaul: true, UnitsPerTrip: 75},
		{ID: "deer", Kind: AcquisitionHunt, Yields: []AcquisitionYield{
			{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: "Leather"}, Count: 5}, UnitValue: 0.5},
			{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: "Meat", Stuff: "Raw"}, Count: 9}}},
			PathDistance: domain.Known(3.0), Labor: domain.Unknown[float64]()},
	} {
		s := SupplyCandidateOfAcquisition(c)
		if lead, _ := s.LeadDays.Value(); lead != 0 || len(s.Yields) != len(c.Yields) {
			t.Fatalf("not a lead-0 one-shot candidate: %+v", s)
		}
		got, ok := AcquisitionCandidateOfSupply(s)
		if !ok || !reflect.DeepEqual(got, c) {
			t.Fatalf("got %+v ok=%v, want %+v", got, ok, c)
		}
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
