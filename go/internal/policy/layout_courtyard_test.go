package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// The courtyard acceptance (#1960, epic #1938): a rich patch that is not a
// rectangle sits in the middle of the best core ground. The generator keeps
// off it rather than wrapping it, so the tests assert the invariants the
// layout/rich-soil case audits natively, not a shape.

// plusPatch is a non-rectangular rich footprint: a plus 20 cells across,
// centred on (70, 70).
func plusPatch(x, z int32) bool {
	return (x >= 60 && x < 80 && z >= 66 && z < 74) || (x >= 66 && x < 74 && z >= 60 && z < 80)
}

func plusSurvey(n int32, dx int32) MapSurvey {
	return zoningSurvey(n, func(x, z int32) SurveyCell {
		if plusPatch(x-dx, z-dx) {
			return SurveyCell{Walkable: true, Fertility: 1.4}
		}
		return SurveyCell{Walkable: true, Fertility: 0.5}
	})
}

func richSet(s MapSurvey) map[domain.Cell]bool {
	rich := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Fertility > zoneRichFertility {
			rich[c.Cell] = true
		}
	}
	return rich
}

func TestCourtyardPlusPatchStaysOneFarmedFieldEnclosedWhole(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := plusSurvey(140, 0)
	rich := richSet(s)
	plan, ok := DeriveLayoutPlan(s, 8, BuildTierCamp, nil, 30).Value()
	if !ok || !plan.Valid() {
		t.Fatal("no valid plan")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			if rich[c] {
				t.Fatalf("%s on rich soil at %v", r.Role, c)
			}
		}
	}
	for _, h := range spineRects(plan.Hallways()) {
		for _, c := range rectCells(h) {
			if rich[c] {
				t.Fatalf("hallway on rich soil at %v", c)
			}
		}
	}
	// The patch is one field zone: every rich cell in the same zone.
	owner, _ := zoningCells(plan.Zones, ZoneField)
	zone := 0
	for c := range rich {
		if owner[c] == 0 || zone != 0 && owner[c] != zone {
			t.Fatalf("rich cell %v in field zone %d, patch zone %d", c, owner[c], zone)
		}
		zone = owner[c]
	}
	// No wall, gate or bridge cell of the perimeter stands on the patch,
	// and the core ring encloses all of it or none (it is farmed, so all).
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePerimeter, ReservePerimeterLight, ReserveOuterWall, ReserveGate, ReserveBridge, ReservePerimeterGap:
			for _, c := range rectCells(r.Area) {
				if rich[c] {
					t.Fatalf("%s on rich soil at %v", r.Kind, c)
				}
			}
		}
	}
	enc := coreEnclosure(plan, 140, 140)
	for c := range rich {
		if !enc.inside(c) {
			t.Fatalf("patch cell %v outside the core enclosure", c)
		}
	}
}

// The farmed patch is not yard room: the yard is sited on open ground.
func TestCourtyardPatchIsNotYardRoom(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := plusSurvey(140, 0)
	rich := richSet(s)
	plan, ok := DeriveLayoutPlan(s, 8, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	yards := plan.YardRooms()
	if len(yards) == 0 {
		t.Fatal("no yard room")
	}
	for _, yard := range yards {
		for _, c := range rectCells(yard.Interior) {
			if rich[c] {
				t.Fatalf("yard room on the farmed patch at %v", c)
			}
		}
	}
}

// 100 colonists beside a patch: wings capped at 10 full ones, suite blocks
// at 6, every route reachable, nothing on the patch. generate runs from the
// centre seed (a full SiteCore on 300x300 takes ~24 s), the patch centred on
// it.
func TestCourtyardHundredColonistsCappedAndReachable(t *testing.T) {
	slowtest.Skip(t, "a 300x300 siting takes ~24 s")
	s := plusSurvey(300, 80)
	rich := richSet(s)
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	lg := g.withObstacles(g.coreObstacles(zones, obstacleAll))
	seed, ok := lg.seed()
	if !ok {
		t.Fatal("no seed")
	}
	targets := make([]float64, 14)
	for i := range targets {
		targets[i] = 60 + float64(i)
	}
	plan := lg.generate(LayoutPlan{Zones: zones}, seed, 100, 1, BuildTierCamp, targets...)
	if !plan.Valid() {
		t.Fatal("invalid")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	for _, h := range spineRects(plan.Hallways()) {
		for _, c := range rectCells(h) {
			if rich[c] {
				t.Fatalf("hallway on rich soil at %v", c)
			}
		}
	}
	for _, r := range plan.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			if rich[c] {
				t.Fatalf("%s on rich soil at %v", r.Role, c)
			}
		}
	}
	if sc := Score(plan, s); len(sc.Missing) != 0 || sc.RoutesErr != "" || sc.RichCells != 0 {
		t.Fatal(sc)
	}
	homes, suites := 0, 0
	for _, w := range plan.Wings {
		switch w.Purpose {
		case WingSuites:
			suites++
			if len(w.Rooms) > suiteMaxRooms {
				t.Fatal("suite block over the cap", len(w.Rooms))
			}
		default:
			homes++
			if len(w.Rooms) > wingMaxRooms {
				t.Fatal("wing over the cap", len(w.Rooms))
			}
		}
		for _, c := range rectCells(wingReserve(w)) {
			if rich[c] {
				t.Fatalf("wing on rich soil at %v", c)
			}
		}
	}
	if homes > 10 || suites > 6 || homes == 0 {
		t.Fatal("wings", homes, "suite blocks", suites)
	}
	if plan.LayoutOutgrown(100) {
		t.Fatal("outgrown")
	}
}
