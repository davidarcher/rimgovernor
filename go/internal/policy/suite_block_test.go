package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// suiteOwnerCase is a plan with one small suite, a's bed standing in it,
// and a target of 50 its floor cannot meet.
func suiteOwnerCase(t *testing.T) (LayoutPlan, RoomObservation, SleepingObservation, map[string]RoomTarget) {
	t.Helper()
	plan := Grow(PlanCore(coreTestZones(), 2, BuildTierCamp), 2, 1, BuildTierCamp, 0.1)
	i := wingOf(plan.Wings, WingSuites)
	if i < 0 {
		t.Fatal("no suite wing")
	}
	s := plan.Wings[i].Rooms[0]
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "s1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"sb"}, Cells: rectCells(s.Interior)}}}
	sleeping := SleepingObservation{Colonists: 1,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("sb")}},
		Beds:   []SleepingBed{{ID: "sb", Definition: "Bed", Room: domain.Known("s1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"a"}, AccessibleTo: []PawnID{"a"}}},
		Rooms:  domain.Known([]UpkeepRoom{{ID: "s1", Role: "Bedroom", Quality: domain.Known(RoomQuality{Space: 5, Impressiveness: 20})}}),
	}
	return plan, rooms, sleeping, map[string]RoomTarget{"s1": {Min: 50}}
}

// Colony growth leaves sited suites alone, and no other room or hallway
// takes a block's ground.
func TestSitedSuiteBlockStaysPutAndKeepsItsGround(t *testing.T) {
	targets := []float64{ImpressivenessDull, ImpressivenessDull}
	p := Grow(PlanCore(coreTestZones(), 2, BuildTierCamp), 2, 1, BuildTierCamp, targets...)
	before := testSuiteWing(t, p)
	g := Grow(p, 10, 2, BuildTierCamp, append(targets, ImpressivenessDecent)...)
	after := g.Wings[wingOf(g.Wings, WingSuites)]
	for i, r := range before.Rooms {
		if !after.Rooms[i].Same(r) {
			t.Fatal("suite moved", r, after.Rooms[i])
		}
	}
	ground := suiteWingGround(after)
	for _, o := range g.AllRooms() {
		if o.Role != ModuleSuite && rectsOverlap(ground, roomWalls(o)) {
			t.Fatal("block ground taken by", o.Role, o.Interior)
		}
	}
	for _, h := range g.Spine {
		for _, s := range spineRects([]SpineSegment{h}) {
			if rectsOverlap(ground, s) && !contains(s, after.Corridor.From) {
				t.Fatal("block ground taken by a hallway", s)
			}
		}
	}
	if _, err := CheckRoutes(g); err != nil {
		t.Fatal("routes:", err)
	}
}

// A built suite whose owner's target rises is never grown: the owner is
// claimed a new suite, and the built one stays exactly as it is.
func TestRisingTargetSitesANewSuiteAndKeepsTheBuiltOne(t *testing.T) {
	plan, rooms, sleeping, targets := suiteOwnerCase(t)
	built := plan.AllRooms()
	claims := SuiteClaims(plan, rooms, sleeping, targets, nil, nil, RoomGate{})
	if len(claims) != 1 || claims[0].Pawn != "a" || claims[0].Bed != "sb" || claims[0].Reason != SuiteClaimTooSmall {
		t.Fatalf("claims = %+v, want a's, too small", claims)
	}
	suites := SuiteTargets(plan, rooms, sleeping, claims)
	if !slices.Equal(suites, []float64{0, 50}) {
		t.Fatalf("suite targets = %v, want the kept suite and a new one at 50", suites)
	}
	grown := Grow(plan, 2, 1, BuildTierCamp, suites...)
	if grown.SuiteRooms() != 2 {
		t.Fatalf("suites = %d, want 2", grown.SuiteRooms())
	}
	for _, r := range built {
		if !slices.ContainsFunc(grown.AllRooms(), r.Same) {
			t.Fatalf("built room %+v moved or changed", r)
		}
	}
	w, d := SuiteSize(50)
	found := false
	for _, r := range grown.AllRooms() {
		if r.Role == ModuleSuite && !slices.ContainsFunc(built, r.Same) {
			found = true
			if r.Interior.Height != w || r.Interior.Width != d {
				t.Fatalf("new suite %+v, want %dx%d", r.Interior, w, d)
			}
		}
	}
	if !found {
		t.Fatal("no new suite")
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal("routes:", err)
	}
}

// Suites for K owners are planned at siting in ceil(K/suiteMaxRooms)
// blocks, each suite sized for its target; sited suites never move and the
// blocks' ground never overlaps another room or block.
func TestSuitesArePlannedInCappedBlocks(t *testing.T) {
	var targets []float64
	for i := 0; i < suiteMaxRooms+3; i++ {
		targets = append(targets, ImpressivenessDull)
	}
	p := Grow(PlanCore(coreTestZones(), 4, BuildTierCamp), 4, 1, BuildTierCamp, targets...)
	blocks := suiteWings(p.Wings)
	if p.SuiteRooms() != len(targets) {
		t.Skipf("only %d of %d suites fit the test core", p.SuiteRooms(), len(targets))
	}
	if want := (len(targets) + suiteMaxRooms - 1) / suiteMaxRooms; len(blocks) != want {
		t.Fatalf("blocks = %d, want %d", len(blocks), want)
	}
	// Index alignment: plan order across blocks is the targets' order.
	var suites []LayoutRoom
	for _, i := range blocks {
		if n := len(p.Wings[i].Rooms); n > suiteMaxRooms {
			t.Fatalf("block of %d suites, cap %d", n, suiteMaxRooms)
		}
		suites = append(suites, p.Wings[i].Rooms...)
	}
	for i, r := range suites {
		if w, d := SuiteSize(targets[i]); r.Interior.Height != w || r.Interior.Width != d {
			t.Fatalf("suite %d is %dx%d, want %dx%d", i, r.Interior.Height, r.Interior.Width, w, d)
		}
	}
	for n, i := range blocks {
		ground := suiteWingGround(p.Wings[i])
		for _, j := range blocks[n+1:] {
			if rectsOverlap(ground, suiteWingGround(p.Wings[j])) {
				t.Fatal("suite blocks overlap", i, j)
			}
		}
		for _, o := range p.Rooms {
			if rectsOverlap(ground, o.Interior) {
				t.Fatal("block ground taken by", o.Role, o.Interior)
			}
		}
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal("routes:", err)
	}
	// A later claim sites a new block and leaves every sited suite alone.
	more := Grow(p, 4, 1, BuildTierCamp, append(append([]float64(nil), make([]float64, len(targets))...), ImpressivenessDecent)...)
	if more.SuiteRooms() == len(targets)+1 {
		for _, r := range suites {
			if !slices.ContainsFunc(more.AllRooms(), r.Same) {
				t.Fatal("sited suite moved", r)
			}
		}
	}
}
