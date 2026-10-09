package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mortarFixture is the corridor fixture with every cell known unroofed but
// the centre of the first row behind the shooters, mortar research done,
// steel for two mortars and the given budget.
func mortarFixture(max int) DefenseRequest {
	r := defenseFixture()
	for i := range r.Cells {
		r.Cells[i].Roofed = domain.Known(r.Cells[i].Cell == domain.Cell{X: 9, Z: 27})
	}
	r.UnitCosts["Turret_Mortar"] = []Amount{{Resource: "Steel", Count: 110}}
	r.Mortar = DefenseMortarRequest{Definition: "Turret_Mortar", Available: domain.Known(true), Stock: domain.Known(map[Resource]int64{"Steel": 250}), Max: max}
	return r
}

func TestMortarBudgetStepsWithRaidPoints(t *testing.T) {
	for _, tc := range []struct {
		points domain.Fact[float64]
		want   int
	}{
		{domain.Unknown[float64](), 0}, {domain.Known(math.NaN()), 0}, {domain.Known(500.0), 0},
		{domain.Known(float64(turretHighPoints)), 1}, {domain.Known(3000.0), 2}, {domain.Known(5000.0), 3},
	} {
		if got := MortarBudget(tc.points); got != tc.want {
			t.Fatalf("%v: %d want %d", tc.points, got, tc.want)
		}
	}
}

func TestDefenseMortarsSiteUnroofedBehindTheLineOffLanes(t *testing.T) {
	r := mortarFixture(2)
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	g := layout.Geometry()
	g.Turrets = cells(13, 27)
	tier, err := DefenseMortars(r, g)
	if err != nil {
		t.Fatal(err)
	}
	if tier.Name != TierMortars || len(tier.Buildings) != 2 {
		t.Fatalf("%+v", tier)
	}
	roof := map[domain.Cell]bool{}
	for _, c := range r.Cells {
		roof[c.Cell] = positive(c.Roofed)
	}
	lanes := map[domain.Cell]bool{}
	for _, c := range append(append([]domain.Cell{}, g.Lanes...), g.Reserved...) {
		lanes[c] = true
	}
	var sited []domain.Cell
	for _, b := range tier.Buildings {
		c := b.Cell()
		if b.Definition() != "Turret_Mortar" || roof[c] || lanes[c] {
			t.Fatalf("mortar %v roofed=%v lane=%v", c, roof[c], lanes[c])
		}
		// Behind the shooters' row (z 23), toward home.
		if c.Z < g.Firing[0].Z+mortarBehindMin {
			t.Fatalf("mortar %v not behind the line", c)
		}
		if tooClose(c, g.Firing) || tooClose(c, g.Turrets) || tooClose(c, sited) {
			t.Fatalf("mortar %v unspaced", c)
		}
		sited = append(sited, c)
	}
	if costs, ok := tier.Costs.Value(); !ok || len(costs) != 1 || costs[0].Count != 220 {
		t.Fatal(tier.Costs)
	}
}

func TestDefenseMortarsGates(t *testing.T) {
	layout, err := DefenseLayouts(mortarFixture(1))
	if err != nil {
		t.Fatal(err)
	}
	g := layout.Geometry()
	for name, mutate := range map[string]func(*DefenseRequest){
		"unresearched": func(r *DefenseRequest) { r.Mortar.Available = domain.Known(false) },
		"no budget":    func(r *DefenseRequest) { r.Mortar.Max = 0 },
		"no steel":     func(r *DefenseRequest) { r.Mortar.Stock = domain.Known(map[Resource]int64{"Steel": 50}) },
		"roof unknown": func(r *DefenseRequest) {
			for i := range r.Cells {
				r.Cells[i].Roofed = domain.Unknown[bool]()
			}
		},
	} {
		r := mortarFixture(1)
		mutate(&r)
		tier, err := DefenseMortars(r, g)
		if err != nil || len(tier.Buildings) != 0 {
			t.Fatalf("%s: %+v %v", name, tier, err)
		}
	}
	if r := mortarFixture(1); !r.MortarGatesOpen() {
		t.Fatal("gates closed")
	}
}
