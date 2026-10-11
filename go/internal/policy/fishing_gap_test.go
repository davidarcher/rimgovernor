package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A large water body can feed far more than a one-worker colony needs. The
// row books labor for the open gap, not for the credited draw.
func TestFishingLaborCappedAtTheGap(t *testing.T) {
	r := fishingRequest()
	r.Regions[0].Population, r.Regions[0].MaxPopulation = domain.Known(520.0), domain.Known(520.0)
	rows, err := FishingChannels(r)
	if err != nil || len(rows) != 1 {
		t.Fatalf("rows=%v err=%v", rows, err)
	}
	full, _ := rows[0].LaborPerDay.Value()
	yield, _ := rows[0].Nutrition().PerDay.Value()
	if math.Abs(yield-3.25) > 1e-9 || math.Abs(full-16250) > 1e-6 {
		t.Fatalf("draw: yield=%v labor=%v", yield, full)
	}
	plan := func(open bool, need float64) FoodPlanEntry {
		row := rows[0]
		row.State = FoodState(domain.Known(open), domain.Unknown[bool]())
		req := FoodPlanRequest{Demand: FoodForecast{RunwayDays: domain.Known(1.0), Consumers: []ConsumerFoodForecast{{ID: "pawn", NutritionPerDay: need}}},
			MinDays: 2, TargetDays: 4, Channels: domain.Known([]SupplyCandidate{row}), Labor: domain.Known(20000.0)}
		p, err := SupplyFoodPlan(req)
		if err != nil || len(p.Portfolio) != 1 {
			t.Fatalf("%v %v", p, err)
		}
		return p.Portfolio[0]
	}
	for _, open := range []bool{false, true} {
		e := plan(open, 1.6)
		share := 0.0
		for _, term := range e.Terms {
			if term.Name == "gap_share" {
				share = term.Value
			}
		}
		// share * 16250 is the booked labor: gap / yield of the draw, 8000.
		// The plan's demand is the need times its target cover (2.8 here).
		if e.Decision == FoodPlanClose || math.Abs(e.DeliveredPerDay-2.8) > 1e-9 || math.Abs(share-2.8/3.25) > 1e-9 {
			t.Fatalf("open=%v: credit is the gap, labor gap/yield of the draw: %+v", open, e)
		}
	}
	// A need above the draw is not scaled.
	if e := plan(false, 5); e.DeliveredPerDay != 3.25 || len(e.Terms) == 0 || e.Terms[len(e.Terms)-1].Name == "gap_share" {
		t.Fatalf("%+v", e)
	}
}
