package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func TestResourceRunwayHistoryWindow(t *testing.T) {
	h := ResourceHistory{Start: 0, End: 20 * 60000, Uses: []ResourceUse{
		{Tick: 5 * 60000, Resource: "Steel", Count: domain.Known(int64(9999))}, // excluded lower boundary
		{Tick: 6 * 60000, Resource: "Steel", Count: domain.Known(int64(100))},
		{Tick: 20 * 60000, Resource: "Steel", Count: domain.Known(int64(50))},
		{Tick: 20*60000 + 1, Resource: "Steel", Count: domain.Known(int64(9999))},
		{Tick: 6 * 60000, Resource: "ComponentIndustrial", Count: domain.Known(int64(15))},
	}}
	for _, tc := range []struct {
		name       string
		stock, ore int64
		days       float64
		deficit    bool
		target     int64
	}{
		{"short", 40, 20, 4, true, 70},
		{"threshold", 40, 30, 5, false, 0},
		{"long", 100, 30, 11, false, 0},
		{"below reserve", 10, 0, 0, true, 70},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := ForecastResourceRunway("Steel", domain.Known(tc.stock), domain.Known(tc.ore), 20, h)
			if n, k := r.ConsumptionPerDay.Value(); !k || n != 10 {
				t.Fatal(r)
			}
			if d, k := r.DaysLeft.Value(); !k || d != tc.days {
				t.Fatal(r)
			}
			if d, k := r.Deficit.Value(); !k || d != tc.deficit || r.Target != tc.target || r.WindowDays != 15 {
				t.Fatal(r)
			}
		})
	}
	r := ForecastResourceRunway("ComponentIndustrial", domain.Known(int64(3)), domain.Known(int64(1)), 0, h)
	if d, k := r.DaysLeft.Value(); !k || d != 4 {
		t.Fatal(r)
	}
}

func TestResourceRunwayUnknownAndZeroRate(t *testing.T) {
	h := ResourceHistory{Start: 10, End: 60010}
	r := ForecastResourceRunway("Steel", domain.Known(int64(50)), domain.Known(int64(0)), 20, h)
	if n, k := r.ConsumptionPerDay.Value(); !k || n != 0 {
		t.Fatal(r)
	}
	if _, k := r.DaysLeft.Value(); k {
		t.Fatal("zero rate has finite runway")
	}
	if d, k := r.Deficit.Value(); !k || d {
		t.Fatal(r)
	}
	for _, count := range []domain.Fact[int64]{domain.Unknown[int64](), domain.Known(int64(-1))} {
		h.Uses = []ResourceUse{{Tick: 11, Resource: "Steel", Count: count}}
		if _, k := ForecastResourceRunway("Steel", domain.Known(int64(50)), domain.Known(int64(0)), 0, h).ConsumptionPerDay.Value(); k {
			t.Fatal("bad history known")
		}
	}
	h.Uses = []ResourceUse{{Tick: 11, Resource: "Steel", Count: domain.Known(int64(10))}}
	r = ForecastResourceRunway("Steel", domain.Known(int64(20)), domain.Unknown[int64](), 0, h)
	if d, k := r.StockDays.Value(); !k || d != 2 {
		t.Fatal(r)
	}
	if _, k := r.DaysLeft.Value(); k {
		t.Fatal("unknown ore treated as zero")
	}
	if _, k := ForecastResourceRunway("Steel", domain.Unknown[int64](), domain.Known(int64(0)), 0, h).DaysLeft.Value(); k {
		t.Fatal("unknown stock")
	}
	h.End = 100
	if _, k := ForecastResourceRunway("Steel", domain.Known(int64(1)), domain.Known(int64(0)), 0, h).ConsumptionPerDay.Value(); k {
		t.Fatal("short history")
	}
	h.End = 0
	if _, k := ForecastResourceRunway("Steel", domain.Known(int64(1)), domain.Known(int64(0)), 0, h).ConsumptionPerDay.Value(); k {
		t.Fatal("rewind")
	}
}

func TestRecipeResourceUseAlternativesAndIterations(t *testing.T) {
	slots := domain.Known([][]Amount{{{Resource: "Steel", Count: 10}, {Resource: "WoodLog", Count: 20}}, {{Resource: "ComponentIndustrial", Count: 2}}})
	if _, k := RecipeResourceUse("Steel", slots, nil, 3).Value(); k {
		t.Fatal("ambiguous stuff")
	}
	if n, k := RecipeResourceUse("ComponentIndustrial", slots, nil, 3).Value(); !k || n != 6 {
		t.Fatal(n, k)
	}
	if n, k := RecipeResourceUse("Steel", slots, []string{"Steel", "ComponentIndustrial"}, 3).Value(); !k || n != 30 {
		t.Fatal(n, k)
	}
	if _, k := RecipeResourceUse("Steel", domain.Known([][]Amount{{{Resource: "Steel", Count: math.MaxInt64}}}), nil, 2).Value(); k {
		t.Fatal("overflow")
	}
}

func TestSurfaceOreOnlySafeMineables(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Method: ResourceSourceMine, Safety: "open_surface", Yield: 20, Designated: true}, {ThingID: "b", Method: ResourceSourceMine, Safety: "roofed", Yield: 100}, {ThingID: "c", Method: "haul", Yield: 40}}
	if n, k := SurfaceOre(sources).Value(); !k || n != 20 {
		t.Fatal(n, k)
	}
	if _, k := SurfaceOre(append(sources, sources[0])).Value(); k {
		t.Fatal("duplicate ore")
	}
}

func TestRunwaySurfacesMaintainResourceDeficit(t *testing.T) {
	p := DefaultRoutinePolicy()
	f := RoutineFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 100}}), ResourceRunways: []ResourceRunway{{Resource: "Steel", DaysLeft: domain.Known(2.0), Deficit: domain.Known(true)}}}
	r, err := DetectRoutine(f, RoutineLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Need != domain.NeedDeficit {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing MaintainResource")
}

func TestUnknownRunwayCannotRecoverMaintenance(t *testing.T) {
	f := RoutineFacts{Resources: domain.Known([]Amount{}), ResourceRunways: []ResourceRunway{{Resource: "Steel", WindowDays: 2}}}
	r, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource {
			if a.Need != domain.NeedUnknown {
				t.Fatal(a)
			}
			return
		}
	}
	t.Fatal("missing resource assessment")
}
