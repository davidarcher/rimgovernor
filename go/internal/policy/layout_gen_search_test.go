package policy

import (
	"reflect"
	"runtime"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func searchFixture(t *testing.T) (coreGrid, planScorer, LayoutPlan, domain.Cell) {
	t.Helper()
	s := courtyardSurvey()
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	seed, ok := g.seed()
	if !ok {
		t.Fatal("no seed")
	}
	base := g.generateBase(LayoutPlan{Zones: zones}, seed, 12, 1, TechTierCamp)
	return g, newPlanScorer(zones, nil, s), base, seed
}

// planStandsClean fails when a plan is not a placeable base: every room
// and its walls on core ground, no two rooms' walls overlapping interiors,
// wings capped, routes valid.
func planStandsClean(t *testing.T, g coreGrid, p LayoutPlan) {
	t.Helper()
	if !p.WithRoomIDs().Valid() {
		t.Fatal("plan is not Valid")
	}
	rooms := p.AllRooms()
	for i, r := range rooms {
		for _, c := range rectCells(roomWalls(r)) {
			if !g.core[c] {
				t.Fatalf("%s wall cell %v off core ground", r.Role, c)
			}
		}
		for _, o := range rooms[i+1:] {
			if rectsOverlap(roomWalls(r), o.Region.Bounds()) || rectsOverlap(r.Region.Bounds(), roomWalls(o)) {
				t.Fatalf("%s and %s overlap", r.Role, o.Role)
			}
		}
	}
	for _, w := range p.Wings {
		if len(w.Rooms) > wingMaxRooms {
			t.Fatal("wing over the cap", len(w.Rooms))
		}
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
}

func TestSearchNeverScoresBelowItsStartAndStaysPlaceable(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	g, sc, base, seed := searchFixture(t)
	planStandsClean(t, g, base)
	got := g.search(sc, base, seed, 60)
	if sc.core(got).Total() < sc.core(base).Total() {
		t.Fatal("search lowered the score")
	}
	planStandsClean(t, g, got)
	if !reflect.DeepEqual(got, g.search(sc, base, seed, 60)) {
		t.Fatal("search is not deterministic")
	}
	if again := g.search(sc, base, seed, 0); !reflect.DeepEqual(again, base) {
		t.Fatal("zero iterations must return the start")
	}
}

// TestSearchOperatorsKeepPlansPlaceable runs each operator on its own: a
// variation it reports must stand clean, and each operator varies the plan
// at least once over a handful of streams.
func TestSearchOperatorsKeepPlansPlaceable(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	g, _, base, seed := searchFixture(t)
	ops := map[string]func(LayoutPlan, *searchRand) (LayoutPlan, bool){
		"moveCluster": g.moveCluster,
		"resiteWing":  g.resiteWing,
	}
	for name, op := range ops {
		moved := 0
		rng := newSearchRand(seed)
		for range 24 {
			out, ok := op(base, &rng)
			if !ok {
				continue
			}
			out = routedEntrances(out)
			if _, err := CheckRoutes(out); err != nil {
				continue // the score ranks a stranded plan below the start
			}
			planStandsClean(t, g, out)
			if !reflect.DeepEqual(out.Rooms, base.Rooms) || !reflect.DeepEqual(out.Wings, base.Wings) {
				moved++
			}
		}
		if moved == 0 {
			t.Errorf("%s never varied the plan", name)
		}
	}
}

func TestTrimSpineKeepsWhatTheBaseNeeds(t *testing.T) {
	g, _, base, _ := searchFixture(t)
	trimmed := base
	trimmed.Spine = trimSpine(base)
	trimmed = routedEntrances(trimmed)
	planStandsClean(t, g, trimmed)
	for i, s := range trimmed.Spine {
		o := base.Spine[i]
		if min(s.From.X, s.To.X) < min(o.From.X, o.To.X) || max(s.From.X, s.To.X) > max(o.From.X, o.To.X) {
			t.Fatal("trim grew a hallway", s, o)
		}
	}
}

// TestSiteCoreIsIdenticalAtAnyThreadCount: a siting pass with the search on
// returns the same plan on one thread and on every thread.
func TestSiteCoreIsIdenticalAtAnyThreadCount(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := courtyardSurvey()
	run := func(procs int) LayoutPlan {
		defer runtime.GOMAXPROCS(runtime.GOMAXPROCS(procs))
		return SiteCore(LayoutPlan{Zones: Zone(s)}, s, 5, 1, TechTierCamp)
	}
	one, many := run(1), run(runtime.NumCPU())
	if !reflect.DeepEqual(one, many) {
		t.Fatal("GOMAXPROCS 1 and many sited different plans")
	}
}

// roomSize is the default until the search picks a variant.
func TestRoomSizeDefaultsAndPicksVariants(t *testing.T) {
	g := coreGrid{}
	if got := g.roomSize(PlannedKitchen); got != coreRoomSize[PlannedKitchen] {
		t.Fatalf("default kitchen %v", got)
	}
	g.sizes = map[PlannedRole]int{PlannedKitchen: 1, PlannedFreezer: 3}
	if got := g.roomSize(PlannedKitchen); got != coreRoomVariants[PlannedKitchen][0] {
		t.Fatalf("variant kitchen %v", got)
	}
	if got := g.roomSize(PlannedFreezer); got != coreRoomSize[PlannedFreezer] {
		t.Fatalf("freezer has no variants, got %v", got)
	}
}

// The local search moves a cluster back in other proportions: over many
// moves the kitchen comes back in both of its shapes, and every plan stays
// routable with all its base rooms.
func TestMoveClusterReproportionsRooms(t *testing.T) {
	g := newCoreGrid(coreTestZones(), nil)
	seed, ok := g.seed()
	if !ok {
		t.Fatal("no seed")
	}
	base := g.generateBase(LayoutPlan{Zones: coreTestZones()}, seed, 3, 0, TechTierCamp)
	shapes := map[[2]int32]bool{}
	for i := range 300 {
		rng := searchRand(i + 1)
		cand, ok := g.moveCluster(base, &rng)
		if !ok {
			continue
		}
		for _, r := range cand.Rooms {
			if r.Role == PlannedKitchen {
				shapes[[2]int32{r.Region.Bounds().Width, r.Region.Bounds().Height}] = true
			}
		}
		if _, err := CheckRoutes(finishForTest(g, cand)); err != nil {
			t.Fatalf("move %d: routes: %v", i, err)
		}
	}
	for _, want := range [][2]int32{coreRoomSize[PlannedKitchen], coreRoomVariants[PlannedKitchen][0]} {
		if !shapes[want] {
			t.Errorf("kitchen never came back %v; saw %v", want, shapes)
		}
	}
}

func finishForTest(g coreGrid, p LayoutPlan) LayoutPlan { return g.finish(routedEntrances(p)) }
