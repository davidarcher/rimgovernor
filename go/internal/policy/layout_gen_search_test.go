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
	base := g.generateBase(LayoutPlan{Zones: zones}, seed, 12, 1, BuildTierCamp)
	return g, newPlanScorer(zones, nil, s), base, seed
}

// planStandsClean fails when a plan is not a placeable base: every room
// and its walls on core ground, no two rooms' walls overlapping interiors,
// wings capped, routes valid.
func planStandsClean(t *testing.T, g coreGrid, p LayoutPlan) {
	t.Helper()
	if !p.Valid() {
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
			if rectsOverlap(roomWalls(r), o.Interior) || rectsOverlap(r.Interior, roomWalls(o)) {
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
		return SiteCore(LayoutPlan{Zones: Zone(s)}, s, 5, 1, BuildTierCamp)
	}
	one, many := run(1), run(runtime.NumCPU())
	if !reflect.DeepEqual(one, many) {
		t.Fatal("GOMAXPROCS 1 and many sited different plans")
	}
}
