package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func reserveFixture() FoodSupply {
	s := foodFixture()
	s.Stocks = []FoodStock{durableFood("ordinary", 12, "", "a", "b"), durableFood("reserve", 9, "", "a", "b")}
	s.Stocks[1].Reserve = true
	s.Stocks[1].DefName = "Pemmican"
	s.Stocks[1].Roofed = domain.Known(true)
	return s
}

func TestReserveExcludedUntilReleased(t *testing.T) {
	s := reserveFixture()
	base := s
	base.Stocks = base.Stocks[:1]
	want, _ := ForecastFood(base, nil)
	got, err := ForecastFood(s, nil)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatal(got, want, err)
	}
	s.Stocks[1].Reserve = false
	got, err = ForecastFood(s, nil)
	if days, _ := got.RunwayDays.Value(); err != nil || days != 7 {
		t.Fatal(got, err)
	}
}

func TestReserveReleasesBelowMinimumEmergencyNeedsNoTimelyChannel(t *testing.T) {
	for _, tc := range []struct {
		name      string
		nutrition float64
		leads     domain.Fact[[]float64]
		emergency bool
	}{
		{"at threshold", 9, domain.Known([]float64{}), false},
		{"below threshold", 8, domain.Known([]float64{}), true},
		{"timely channel", 8, domain.Known([]float64{1}), false},
		{"too late", 8, domain.Known([]float64{3}), true},
		{"unknown channels", 8, domain.Unknown[[]float64](), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := reserveFixture()
			s.Stocks[0].Nutrition = domain.Known(tc.nutrition)
			r, err := ReviewFoodReserve(s, nil, 5, 3, tc.leads)
			// Any runway under the minimum releases the reserve to eat; only
			// the emergency flag still waits on timely channels.
			if err != nil || r.Emergency != tc.emergency || (len(r.Release) > 0) != (tc.nutrition < 9) {
				t.Fatal(r, err)
			}
		})
	}
}

func TestReserveBillTargetsObservedDeficitNotOtherBillPromises(t *testing.T) {
	r, err := ReviewFoodReserve(reserveFixture(), nil, 5, 3, domain.Known([]float64{}))
	if err != nil || r.TargetNutrition != 15 || r.DeficitNutrition != 6 {
		t.Fatal(r, err)
	}
	benches := domain.Known([]ProductionBench{{ID: "stove", Token: domain.Known("token"), Usable: domain.Known(true), Recipes: []ProductionRecipe{
		{Name: "MakePemmican", Available: domain.Known(true), Products: []ProductionProduct{{Name: "Pemmican", Nutrition: domain.Known(.05), Edible: domain.Known(true)}}},
		{Name: "CookMealSurvival", Available: domain.Known(true), Products: []ProductionProduct{{Name: "MealSurvivalPack", Nutrition: domain.Known(.9), Edible: domain.Known(true)}}},
	}}})
	b, ok := SelectReserveBill(benches, r)
	if !ok || b.Recipe != "CookMealSurvival" || b.Target != 7 {
		t.Fatal(b, ok)
	}
	rows, _ := benches.Value()
	rows[0].Recipes[1].Available = domain.Known(false)
	b, ok = SelectReserveBill(domain.Known(rows), r)
	if !ok || b.Recipe != "MakePemmican" || b.Target != 300 {
		t.Fatal(b, ok)
	}
	r.DeficitNutrition = 0
	if _, ok = SelectReserveBill(benches, r); ok {
		t.Fatal("full reserve created a bill")
	}
}

func TestReleasedReserveDoesNotImmediatelyBecomeHeld(t *testing.T) {
	s := reserveFixture()
	s.Stocks[0].Nutrition = domain.Known(0.)
	s.Stocks[1].Reserve = false
	unknown, err := ReviewFoodReserve(s, nil, 5, 3, domain.Unknown[[]float64]())
	if err != nil || len(unknown.Hold) != 0 {
		t.Fatal("unknown channels cannot justify holding the only food", unknown, err)
	}
	r, err := ReviewFoodReserve(s, nil, 5, 3, domain.Known([]float64{}))
	if err != nil || !r.Emergency || len(r.Hold) != 0 {
		t.Fatal(r, err)
	}
	s.Stocks[0].Nutrition = domain.Known(12.)
	r, err = ReviewFoodReserve(s, nil, 5, 3, domain.Known([]float64{}))
	if err != nil || r.Emergency || !reflect.DeepEqual(r.Hold, []string{"reserve"}) {
		t.Fatal(r, err)
	}
}

func TestDropReserveHeldKeepsSuppliesOffReserveFood(t *testing.T) {
	rows := []StartingSupply{
		{Thing: "held", Definition: "Pemmican"},
		{Thing: "hold-next", Definition: "Steel"},
		{Thing: "released", Definition: "Pemmican"},
		{Thing: "steel", Definition: "Steel"},
		{Thing: "unsafe", Definition: "Pemmican", Forbid: true},
	}
	if got := DropReserveHeld(rows, domain.Unknown[FoodReserveReview]()); len(got) != len(rows) {
		t.Fatalf("unknown reserve dropped rows: %v", got)
	}
	got := DropReserveHeld(rows, domain.Known(FoodReserveReview{Hold: []string{"hold-next"}, Release: []string{"released"}}))
	var things []string
	for _, row := range got {
		things = append(things, row.Thing)
	}
	if want := []string{"released", "steel", "unsafe"}; !reflect.DeepEqual(things, want) {
		t.Fatalf("kept %v, want %v", things, want)
	}
}
