package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestAnimalProductWait: a herd whose products are already delivering
// lends game time to the clock (#577); an unknown plan or one without an
// open animal-product channel lends none.
func TestAnimalProductWait(t *testing.T) {
	if got := animalProductWait(domain.Unknown[policy.FoodPlan]()); got != 0 {
		t.Fatalf("unknown plan lent %d ticks", got)
	}
	closed := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: policy.FoodChannel{Kind: policy.FoodAnimalProduct, ID: "Cow"}, Decision: policy.FoodPlanHold}}}
	if got := animalProductWait(domain.Known(closed)); got != 0 {
		t.Fatalf("closed channel lent %d ticks", got)
	}
	open := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{
		{Channel: policy.FoodChannel{Kind: policy.FoodCrop, ID: "Rice"}, DeliveredPerDay: 2},
		{Channel: policy.FoodChannel{Kind: policy.FoodAnimalProduct, ID: "Chicken"}, Decision: policy.FoodPlanHold, Reason: "already delivering", DeliveredPerDay: 1},
	}}
	if got := animalProductWait(domain.Known(open)); got != stockWaitTicks {
		t.Fatalf("open channel lent %d ticks, want %d", got, stockWaitTicks)
	}
}
