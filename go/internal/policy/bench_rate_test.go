package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func kf(v float64) domain.Fact[float64] { return domain.Known(v) }

func benchWorkers(n int, hours, speed float64) []BenchWorker {
	out := make([]BenchWorker, n)
	for i := range out {
		out[i] = BenchWorker{WorkHoursPerDay: kf(hours), WorkSpeed: kf(speed)}
	}
	return out
}

func TestDemandRate(t *testing.T) {
	un := domain.Unknown[float64]()
	tests := []struct {
		name    string
		deficit domain.Fact[float64]
		class   ResourceClass
		want    float64
		known   bool
	}{
		{"food one day", kf(30), ResourceFood, 30, true},
		{"material three days", kf(90), ResourceMaterial, 30, true},
		{"no deficit", kf(-5), ResourceFood, 0, true},
		{"unknown deficit", un, ResourceFood, 0, false},
		{"unknown class", kf(10), "mystery", 0, false},
		{"nan deficit", kf(math.NaN()), ResourceFood, 0, false},
	}
	for _, tc := range tests {
		got, ok := DemandRate(tc.deficit, tc.class).Value()
		if ok != tc.known || (ok && math.Abs(got-tc.want) > 1e-9) {
			t.Errorf("%s: got (%v,%v) want (%v,%v)", tc.name, got, ok, tc.want, tc.known)
		}
	}
}

func TestBenchCapacity(t *testing.T) {
	un := domain.Unknown[float64]()
	// 2500 ticks/hour: one baseline worker for 8 hours at a 1.0 bench makes
	// 20000 work ticks a day, i.e. 10 units of a 2000-tick recipe.
	r := BenchRecipe{WorkPerUnit: kf(2000), BenchWorkSpeed: kf(1)}
	mixed := []BenchWorker{
		{kf(8), kf(1)},
		{kf(8), kf(0.5)},
		{kf(4), kf(2)},
	}
	tests := []struct {
		name  string
		r     BenchRecipe
		w     []BenchWorker
		want  float64
		known bool
	}{
		{"one worker", r, benchWorkers(1, 8, 1), 10, true},
		{"three workers", r, benchWorkers(3, 8, 1), 30, true},
		{"mixed skills", r, mixed, 10 + 5 + 10, true},
		{"bench speed", BenchRecipe{kf(2000), kf(0.5)}, benchWorkers(1, 8, 1), 5, true},
		{"no workers", r, nil, 0, true},
		{"unknown recipe work", BenchRecipe{un, kf(1)}, benchWorkers(1, 8, 1), 0, false},
		{"unknown bench speed", BenchRecipe{kf(2000), un}, benchWorkers(1, 8, 1), 0, false},
		{"zero recipe work", BenchRecipe{kf(0), kf(1)}, benchWorkers(1, 8, 1), 0, false},
		{"unknown worker hours", r, []BenchWorker{{kf(8), kf(1)}, {un, kf(1)}}, 0, false},
		{"unknown worker speed", r, []BenchWorker{{kf(8), un}}, 0, false},
		{"negative hours", r, benchWorkers(1, -1, 1), 0, false},
	}
	for _, tc := range tests {
		got, ok := BenchCapacity(tc.r, tc.w).Value()
		if ok != tc.known || (ok && math.Abs(got-tc.want) > 1e-9) {
			t.Errorf("%s: got (%v,%v) want (%v,%v)", tc.name, got, ok, tc.want, tc.known)
		}
	}
}

func TestBenchesWanted(t *testing.T) {
	un := domain.Unknown[float64]()
	tests := []struct {
		name  string
		d, c  domain.Fact[float64]
		want  int
		known bool
	}{
		{"exact", kf(20), kf(10), 2, true},
		{"rounds up", kf(21), kf(10), 3, true},
		{"float fuzz", kf(0.3), kf(0.1), 3, true},
		{"zero demand", kf(0), kf(10), 0, true},
		{"zero demand zero capacity", kf(0), kf(0), 0, true},
		{"demand without capacity", kf(5), kf(0), 0, false},
		{"unknown demand", un, kf(10), 0, false},
		{"unknown capacity", kf(5), un, 0, false},
		{"negative demand", kf(-1), kf(10), 0, false},
	}
	for _, tc := range tests {
		got, ok := BenchesWanted(tc.d, tc.c).Value()
		if ok != tc.known || (ok && got != tc.want) {
			t.Errorf("%s: got (%v,%v) want (%v,%v)", tc.name, got, ok, tc.want, tc.known)
		}
	}
}

// A 100-pawn colony needing 300 meals a day; a simple meal is 400 work
// ticks. Each 1.0-speed stove has 2 cooks working 6 hours at speed 1.
func TestBenchModelHundredPawns(t *testing.T) {
	demand := DemandRate(kf(100*3), ResourceFood)
	capacity := BenchCapacity(BenchRecipe{kf(400), kf(1)}, benchWorkers(2, 6, 1))
	// 2 cooks x 6h x 2500 / 400 = 75 meals per stove.
	if c, _ := capacity.Value(); c != 75 {
		t.Fatalf("capacity = %v, want 75", c)
	}
	got, ok := BenchesWanted(demand, capacity).Value()
	if !ok || got != 4 {
		t.Fatalf("benches = (%v,%v), want 4", got, ok)
	}
}
