package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func larderCorpse(id string, forbidden bool, meat, size float64) FoodStock {
	return FoodStock{ID: id, DefName: "Corpse_Muffalo", Holder: domain.Known(PawnID("")), Nutrition: domain.Known(meat * 0.05), Eaters: []PawnID{"a"}, Perishable: domain.Known(true), RotTicks: domain.Known(int64(600000)), Roofed: domain.Known(true), TemperatureC: domain.Known(-5.), Room: domain.Known("freezer"), Corpse: true, Forbidden: domain.Known(forbidden), MeatAmount: domain.Known(meat), BodySize: domain.Known(size), TileFootprint: domain.Known(int64(1))}
}
func larderObservation(stocks ...FoodStock) FoodStorageObservation {
	v := FoodStorageStocks(FoodSupply{Stocks: stocks})
	l := FoodLarder{RawMeatNutrition: 3, CookDemandNutrition: 0.5}
	for _, s := range stocks {
		l.Corpses = append(l.Corpses, CorpseHandling{ID: s.ID, Cell: domain.Cell{X: 2, Z: 3}})
	}
	v.Larder = domain.Known(l)
	return v
}
func TestLarderHandlingDoesNotWaitForDevelopmentSlot(t *testing.T) {
	f := stableRoutine()
	f.FoodStorageUpkeep = larderObservation(larderCorpse("a", false, 300, 2))
	r := needs(t, f, RoutineLatches{})
	for _, g := range r.Goals {
		if g.ID == MaintainFoodStorage {
			if g.Priority != 2 || g.MethodUnavailable {
				t.Fatal("larder handling must be available as food upkeep", g)
			}
			return
		}
	}
	t.Fatal("missing larder goal")
}
func TestCorpseDensityThresholds(t *testing.T) {
	for _, tc := range []struct {
		meat, size float64
		hold       bool
	}{{225, 2, false}, {226, 2, true}, {75, 0.75, false}, {76, 0.75, true}, {100, 0.76, false}, {100, 0.5, true}} {
		s := larderCorpse("a", false, tc.meat, tc.size)
		got, err := SelectCorpseLarder(larderObservation(s))
		if err != nil || (got.Kind == "forbid") != tc.hold {
			t.Fatalf("%+v: %+v %v", tc, got, err)
		}
	}
}
func TestLarderReleasesOldestAndWaitsForButchering(t *testing.T) {
	a, b, c := larderCorpse("a", true, 300, 2), larderCorpse("b", true, 300, 2), larderCorpse("c", true, 300, 2)
	b.RotTicks = domain.Known(int64(300000))
	v := larderObservation(c, a, b)
	l, _ := v.Larder.Value()
	l.RawMeatNutrition = 0.1
	v.Larder = domain.Known(l)
	got, err := SelectCorpseLarder(v)
	if err != nil || got.Kind != "allow" || got.Stock.ID != "b" {
		t.Fatal(got, err)
	}
	rows, _ := v.Stocks.Value()
	rows[2].Stock.Forbidden = domain.Known(false)
	v.Stocks = domain.Known(rows)
	got, err = SelectCorpseLarder(v)
	if err != nil || got.Kind != "" {
		t.Fatal("released corpse must fund the window until butchered", got, err)
	}
	l.RawMeatNutrition = 0.5
	v.Larder = domain.Known(l)
	got, err = SelectCorpseLarder(v)
	if err != nil || got.Kind != "forbid" {
		t.Fatal("window equality may hold the reserve", got, err)
	}
}
func TestLarderNeverHoldsWarmAndReleasesNearRot(t *testing.T) {
	for _, tc := range []struct {
		name  string
		temp  float64
		roof  bool
		ticks int64
	}{{"warm", 1, true, 600000}, {"unroofed", -5, false, 600000}, {"near rot", -5, true, 15000}} {
		t.Run(tc.name, func(t *testing.T) {
			s := larderCorpse("a", true, 300, 2)
			s.TemperatureC = domain.Known(tc.temp)
			s.Roofed = domain.Known(tc.roof)
			s.RotTicks = domain.Known(tc.ticks)
			got, err := SelectCorpseLarder(larderObservation(s))
			if err != nil || got.Kind != "allow" {
				t.Fatal(got, err)
			}
			s.Forbidden = domain.Known(false)
			got, err = SelectCorpseLarder(larderObservation(s))
			if err != nil || got.Kind == "forbid" {
				t.Fatal(got, err)
			}
		})
	}
}
func TestLarderUnforbidBeforeHaulAndObserveBeforeHold(t *testing.T) {
	s := larderCorpse("a", true, 300, 2)
	s.TemperatureC = domain.Known(20.)
	v := larderObservation(s)
	l, _ := v.Larder.Value()
	l.Corpses[0].Hauler = "hauler"
	l.Corpses[0].FrozenDestination = true
	v.Larder = domain.Known(l)
	got, err := SelectCorpseLarder(v)
	if err != nil || got.Kind != "allow" {
		t.Fatal(got, err)
	}
	s.Forbidden = domain.Known(false)
	v.Stocks = domain.Known([]FoodStorageStock{{Stock: s}})
	got, err = SelectCorpseLarder(v)
	if err != nil || got.Kind != "haul" {
		t.Fatal(got, err)
	}
	s.TemperatureC = domain.Known(-5.)
	v.Stocks = domain.Known([]FoodStorageStock{{Stock: s}})
	got, err = SelectCorpseLarder(v)
	if err != nil || got.Kind != "forbid" {
		t.Fatal(got, err)
	}
}
func TestCorpseForecastReserveAndMeatNutrition(t *testing.T) {
	s := FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "a", NutritionPerDay: domain.Known(1.)}}, Stocks: []FoodStock{larderCorpse("a", false, 300, 2)}}
	result, err := ForecastFood(s, nil)
	if err != nil || result.UsableNutrition != 10 || result.AtRiskNutrition != 5 {
		t.Fatal(result, err)
	}
	s.Stocks[0].Forbidden = domain.Known(true)
	result, err = ForecastFood(s, nil)
	if err != nil || result.UsableNutrition != 0 || result.AtRiskNutrition != 0 {
		t.Fatal(result, err)
	}
	s.Stocks[0].RotTicks = domain.Unknown[int64]()
	if _, err = ForecastFood(s, nil); err == nil {
		t.Fatal("reserve must still validate its facts")
	}
}
func TestFoodStorageLarderRunsWithoutStorageDeficit(t *testing.T) {
	v := larderObservation(larderCorpse("a", false, 300, 2))
	got, err := ReviewFoodStorage(v, false, DefaultFoodStoragePolicy())
	if err != nil || !got.Active {
		t.Fatal(got, err)
	}
}

func TestCorpseWithoutEligibleEatersRemainsVisibleToLarder(t *testing.T) {
	corpse := larderCorpse("a", true, 300, 2)
	corpse.Eaters = nil
	supply := FoodSupply{Complete: domain.Known(true), Consumers: []FoodConsumer{{ID: "a", NutritionPerDay: domain.Known(1.)}}, Stocks: []FoodStock{corpse}}
	forecast, err := ForecastFood(supply, nil)
	if err != nil || forecast.UsableNutrition != 0 {
		t.Fatal(forecast, err)
	}
	v := larderObservation(corpse)
	l, _ := v.Larder.Value()
	l.RawMeatNutrition = 0
	v.Larder = domain.Known(l)
	method, err := SelectCorpseLarder(v)
	if err != nil || method.Kind != "allow" {
		t.Fatal(method, err)
	}
}

func TestCarriedCorpseStillFundsCookWindow(t *testing.T) {
	carried := larderCorpse("carried", false, 300, 2)
	carried.Roofed = domain.Unknown[bool]()
	carried.Room = domain.Unknown[string]()
	v := larderObservation(larderCorpse("stored", true, 300, 2), carried)
	l, _ := v.Larder.Value()
	l.RawMeatNutrition = 0
	l.Corpses = l.Corpses[:1]
	v.Larder = domain.Known(l)
	method, err := SelectCorpseLarder(v)
	if err != nil || method.Kind != "" {
		t.Fatal(method, err)
	}
}
