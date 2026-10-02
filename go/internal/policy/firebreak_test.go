package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const firebreakDay = domain.Tick(60000)

func firebreakFixture(t *testing.T, points ...domain.Cell) FirebreakRequest {
	t.Helper()
	// One wall building covers every point, so a base of many cells keeps
	// one valid identity.
	e := extentFixture(t, points[0])
	census, _ := e.Construction.Value()
	home, _ := e.Home.Value()
	census.Buildings[0].Cells, home.Targets[0].Cells = points, points
	e.Construction, e.Home = domain.Known(census), domain.Known(home)
	ground := map[domain.Cell]domain.Fact[FirebreakGround]{}
	for x := int32(0); x < 60; x++ {
		for z := int32(0); z < 60; z++ {
			ground[domain.Cell{X: x, Z: z}] = domain.Known(FirebreakOpen)
		}
	}
	return FirebreakRequest{
		Bounds: domain.Known(Bounds{Width: 60, Height: 60}), Construction: e.Construction, Claims: domain.Known([]ConstructionClaim{}), Home: e.Home,
		GrowingZones: domain.Known([]domain.Cell{}), Planned: domain.Known([]domain.Cell{}), Ground: ground,
		Stage: domain.Known(StageDevelopment), Now: 10 * firebreakDay,
		Floors: map[string]FloorDefinition{
			"Concrete":       firebreakFloorDef(0, 1, 10),
			"PavedTile":      firebreakFloorDef(0, 5, 10),
			"WoodPlankFloor": firebreakFloorDef(0.22, 1, 1),
		},
		Stock:   domain.Known(map[Resource]int64{"Steel": 1000}),
		Claimed: domain.Known(map[Resource]int64{}),
		Policy:  DefaultFlooringPolicy(),
	}
}

func firebreakFloorDef(flammability float64, steel int64, work float64) FloorDefinition {
	return FloorDefinition{
		Available: domain.Known(true), Terrain: domain.Known(true), Flammability: domain.Known(flammability),
		PathCost: domain.Known(int32(0)), Costs: domain.Known([]Amount{{Resource: "Steel", Count: steel}}), WorkToBuild: domain.Known(work),
	}
}

func firebreakRun(t *testing.T, r FirebreakRequest, dwell map[domain.Cell]domain.Tick) (FirebreakPlan, map[domain.Cell]domain.Tick) {
	t.Helper()
	got, next, err := PlanFirebreak(r, dwell)
	plan, known := got.Value()
	if err != nil || !known {
		t.Fatal(known, err)
	}
	return plan, next
}

func ringCells(p FirebreakPlan) map[domain.Cell]FirebreakTreatment {
	m := map[domain.Cell]FirebreakTreatment{}
	for _, c := range p.Cells {
		m[c.Cell] = c.Treatment
	}
	return m
}

// chebyshevBand lists the cells within width of footprint but outside it.
func chebyshevBand(footprint map[domain.Cell]bool, width int32) map[domain.Cell]bool {
	band := map[domain.Cell]bool{}
	for c := range footprint {
		for dx := -width; dx <= width; dx++ {
			for dz := -width; dz <= width; dz++ {
				n := domain.Cell{X: c.X + dx, Z: c.Z + dz}
				if !footprint[n] {
					band[n] = true
				}
			}
		}
	}
	return band
}

func square(footprint map[domain.Cell]bool, x0, z0, x1, z1 int32) {
	for x := x0; x <= x1; x++ {
		for z := z0; z <= z1; z++ {
			footprint[domain.Cell{X: x, Z: z}] = true
		}
	}
}

// squarePoints lists every cell of the inclusive rectangle: walls that make
// the base footprint itself, since the ring hugs the extent (margin 0).
func squarePoints(x0, z0, x1, z1 int32) []domain.Cell {
	var out []domain.Cell
	for x := x0; x <= x1; x++ {
		for z := z0; z <= z1; z++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

func TestFirebreakBandGeometry(t *testing.T) {
	rect := map[domain.Cell]bool{}
	square(rect, 26, 26, 34, 34)
	ell := map[domain.Cell]bool{}
	square(ell, 16, 16, 24, 32)
	square(ell, 16, 16, 34, 24)
	fields := map[domain.Cell]bool{}
	square(fields, 26, 26, 34, 34)
	square(fields, 35, 28, 38, 31)
	ellPoints := append(squarePoints(16, 16, 24, 32), squarePoints(25, 16, 34, 24)...)
	for _, tt := range []struct {
		name      string
		points    []domain.Cell
		zones     []domain.Cell
		footprint map[domain.Cell]bool
	}{
		{"rectangle", squarePoints(26, 26, 34, 34), nil, rect},
		{"l-shape", ellPoints, nil, ell},
		{"growing zone enclosed", squarePoints(26, 26, 34, 34), []domain.Cell{{X: 35, Z: 28}, {X: 38, Z: 31}, {X: 36, Z: 29}, {X: 37, Z: 30}, {X: 35, Z: 29}, {X: 35, Z: 30}, {X: 35, Z: 31}, {X: 36, Z: 28}, {X: 36, Z: 30}, {X: 36, Z: 31}, {X: 37, Z: 28}, {X: 37, Z: 29}, {X: 37, Z: 31}, {X: 38, Z: 28}, {X: 38, Z: 29}, {X: 38, Z: 30}}, fields},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := firebreakFixture(t, tt.points...)
			r.GrowingZones = domain.Known(tt.zones)
			plan, _ := firebreakRun(t, r, nil)
			got := ringCells(plan)
			want := chebyshevBand(tt.footprint, FirebreakWidth)
			if len(got) != len(want) {
				t.Fatal(len(got), len(want))
			}
			for c := range want {
				if got[c] != FirebreakCut {
					t.Fatalf("%v: %q", c, got[c])
				}
			}
		})
	}
}

func TestFirebreakSkips(t *testing.T) {
	c := domain.Cell{X: 25, Z: 30}
	for _, tt := range []struct {
		name  string
		edit  func(*FirebreakRequest)
		skip  bool
		decon bool
	}{
		{"open", func(*FirebreakRequest) {}, false, false},
		{"player wall", func(r *FirebreakRequest) { r.Ground[c] = domain.Known(FirebreakPlayerEdifice) }, true, false},
		{"water", func(r *FirebreakRequest) { r.Ground[c] = domain.Known(FirebreakWater) }, true, false},
		{"natural rock", func(r *FirebreakRequest) { r.Ground[c] = domain.Known(FirebreakNaturalRock) }, true, false},
		{"stone ruin", func(r *FirebreakRequest) { r.Ground[c] = domain.Known(FirebreakStoneRuin) }, true, false},
		{"layout plan", func(r *FirebreakRequest) { r.Planned = domain.Known([]domain.Cell{c}) }, true, false},
		{"construction claim", func(r *FirebreakRequest) {
			r.Claims = domain.Known([]ConstructionClaim{{Plan: "p", Cells: []domain.Cell{c}}})
		}, true, false},
		{"wooden ruin", func(r *FirebreakRequest) { r.Ground[c] = domain.Known(FirebreakWoodenRuin) }, false, true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := firebreakFixture(t, squarePoints(26, 26, 34, 34)...)
			tt.edit(&r)
			plan, next := firebreakRun(t, r, nil)
			_, ring := ringCells(plan)[c]
			_, tracked := next[c]
			if ring == tt.skip || tracked == tt.skip || slices.Contains(plan.Deconstruct, c) != tt.decon {
				t.Fatal(ring, tracked, plan.Deconstruct)
			}
		})
	}
}

func TestFirebreakCutUntilSettled(t *testing.T) {
	c := domain.Cell{X: 25, Z: 30}
	settled := func(r FirebreakRequest) map[domain.Cell]domain.Tick {
		dwell := map[domain.Cell]domain.Tick{}
		for x := int32(20); x < 40; x++ {
			for z := int32(20); z < 40; z++ {
				dwell[domain.Cell{X: x, Z: z}] = r.Now - FirebreakSettleTicks
			}
		}
		return dwell
	}
	for _, tt := range []struct {
		name string
		edit func(*FirebreakRequest, map[domain.Cell]domain.Tick)
		want FirebreakTreatment
	}{
		{"all hold", func(*FirebreakRequest, map[domain.Cell]domain.Tick) {}, FirebreakPave},
		{"stage", func(r *FirebreakRequest, _ map[domain.Cell]domain.Tick) { r.Stage = domain.Known(StageStable) }, FirebreakCut},
		{"dwell", func(r *FirebreakRequest, d map[domain.Cell]domain.Tick) { d[c] = r.Now - FirebreakSettleTicks + 1 }, FirebreakCut},
		{"stock after claims", func(r *FirebreakRequest, _ map[domain.Cell]domain.Tick) {
			r.Claimed = domain.Known(map[Resource]int64{"Steel": 950})
		}, FirebreakCut},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := firebreakFixture(t, squarePoints(26, 26, 34, 34)...)
			dwell := settled(r)
			tt.edit(&r, dwell)
			plan, _ := firebreakRun(t, r, dwell)
			if got := ringCells(plan)[c]; got != tt.want {
				t.Fatal(got)
			}
		})
	}
}

func TestFirebreakDwellRestartsAfterLeaving(t *testing.T) {
	c := domain.Cell{X: 25, Z: 30}
	r := firebreakFixture(t, squarePoints(26, 26, 34, 34)...)
	r.Now = firebreakDay
	_, dwell := firebreakRun(t, r, nil)
	if dwell[c] != firebreakDay {
		t.Fatal(dwell[c])
	}
	r.Now, r.Planned = 2*firebreakDay, domain.Known([]domain.Cell{c})
	_, dwell = firebreakRun(t, r, dwell)
	if _, ok := dwell[c]; ok {
		t.Fatal("kept a cell that left the ring")
	}
	r.Now, r.Planned = 8*firebreakDay, domain.Known([]domain.Cell{})
	plan, dwell := firebreakRun(t, r, dwell)
	if dwell[c] != 8*firebreakDay || ringCells(plan)[c] != FirebreakCut {
		t.Fatal(dwell[c], ringCells(plan)[c])
	}
}

func TestFirebreakUnknownInputs(t *testing.T) {
	c := domain.Cell{X: 25, Z: 30}
	for _, tt := range []struct {
		name string
		edit func(*FirebreakRequest)
	}{
		{"growing zones", func(r *FirebreakRequest) { r.GrowingZones = domain.Unknown[[]domain.Cell]() }},
		{"planned", func(r *FirebreakRequest) { r.Planned = domain.Unknown[[]domain.Cell]() }},
		{"stage", func(r *FirebreakRequest) { r.Stage = domain.Unknown[ColonyStage]() }},
		{"home", func(r *FirebreakRequest) { r.Home = domain.Unknown[HomeCoverageObservation]() }},
		{"ground", func(r *FirebreakRequest) { r.Ground[c] = domain.Unknown[FirebreakGround]() }},
		{"ground missing", func(r *FirebreakRequest) { delete(r.Ground, c) }},
		{"stock when settled", func(r *FirebreakRequest) { r.Stock = domain.Unknown[map[Resource]int64]() }},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := firebreakFixture(t, squarePoints(26, 26, 34, 34)...)
			dwell := map[domain.Cell]domain.Tick{c: 0}
			tt.edit(&r)
			got, next, err := PlanFirebreak(r, dwell)
			if _, known := got.Value(); err != nil || known || next[c] != 0 || len(next) != 1 {
				t.Fatal(known, err, len(next))
			}
		})
	}
}
