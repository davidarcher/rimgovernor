package combatsim

import (
	"math"
	"math/rand/v2"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// oracleFlood is the per-call breadth-first flood the enemy AI used before
// component labelling: the reference the labelled lookup must equal exactly.
func oracleFlood(g *grid, from domain.Cell) []bool {
	reach := make([]bool, g.w*g.h)
	if !g.walkable(from) {
		return reach
	}
	q := []int32{int32(g.idx(from))}
	reach[q[0]] = true
	for h := 0; h < len(q); h++ {
		cc := g.cell(int(q[h]))
		for k, off := range adjacent {
			nb := add(cc, off)
			if !g.walkable(nb) {
				continue
			}
			if k >= 4 && (!g.walkable(domain.Cell{X: nb.X, Z: cc.Z}) || !g.walkable(domain.Cell{X: cc.X, Z: nb.Z})) {
				continue
			}
			if ni := g.idx(nb); !reach[ni] {
				reach[ni] = true
				q = append(q, int32(ni))
			}
		}
	}
	return reach
}

func randomGrid(r *rand.Rand) *grid {
	w, h := 1+r.IntN(40), 1+r.IntN(40)
	g := &grid{
		w: w, h: h,
		terrain: []TerrainDef{
			{Name: "soil", PathCost: 2, Passable: true},
			{Name: "water", PathCost: 50, Passable: false},
			{Name: "marsh", PathCost: 20, Passable: true},
		},
		things: []ThingDef{
			{},
			{Name: "wall", Fillage: FillFull, Passability: Impassable},
			{Name: "door", Passability: PassThroughOnly, PathCost: 45},
			{Name: "crate", PathCost: 14},
		},
		terrainI: make([]uint8, w*h), thingI: make([]uint8, w*h),
		roofed: make([]bool, w*h), doorOpen: make([]bool, w*h),
	}
	// Vary the density so grids range from open to heavily partitioned.
	wallP, waterP := r.Float64()*0.6, r.Float64()*0.3
	for i := range w * h {
		switch x := r.Float64(); {
		case x < wallP:
			g.thingI[i] = 1
		case x < wallP+0.05:
			g.thingI[i] = 2
		case x < wallP+0.1:
			g.thingI[i] = 3
		}
		if r.Float64() < waterP {
			g.terrainI[i] = 1
		} else if r.IntN(5) == 0 {
			g.terrainI[i] = 2
		}
	}
	return g
}

func TestFloodMatchesTheOriginalBreadthFirstFlood(t *testing.T) {
	r := rand.New(rand.NewPCG(1, 2913))
	for trial := range 600 {
		g := randomGrid(r)
		e := &EnemyAI{sim: &Sim{g: *g}}
		froms := []domain.Cell{{X: -1, Z: 0}, {X: 0, Z: -1}, {X: int32(g.w), Z: 0}, {X: 0, Z: int32(g.h)}}
		for i := range g.w * g.h {
			froms = append(froms, g.cell(i))
		}
		for _, from := range froms {
			want := oracleFlood(g, from)
			got := e.flood(from)
			for i := range want {
				if got.has(i) != want[i] {
					t.Fatalf("trial %d (%dx%d) from %v: cell %v reached = %v, original flood %v", trial, g.w, g.h, from, g.cell(i), got.has(i), want[i])
				}
			}
		}
	}
}

func TestDistanceDecayIsMathPow(t *testing.T) {
	check := func(d int) {
		t.Helper()
		if got, want := distanceDecay(d), math.Pow(0.967, math.Sqrt(float64(d))); math.Float64bits(got) != math.Float64bits(want) {
			t.Fatalf("distanceDecay(%d) = %v, want %v", d, got, want)
		}
	}
	for d := range 20000 { // grows the shared table past its first size
		check(d)
	}
	for d := 19999; d >= 0; d -= 7 {
		check(d)
	}
}
