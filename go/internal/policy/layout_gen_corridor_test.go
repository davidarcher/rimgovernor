package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func openGround(x, z, w, h int32) coreGrid {
	g := coreGrid{core: map[domain.Cell]bool{}, rock: map[domain.Cell]bool{}}
	for _, c := range rectCells(Rectangle{X: x, Z: z, Width: w, Height: h}) {
		g.core[c] = true
	}
	return g
}

// ringPlan is an H of hallways with the kitchen and the freezer at the far
// ends of the two crossings: the only way between them is down one
// crossing, along the main hallway and up the other, unless a ring joins
// the crossings' north ends.
func ringPlan(farApart bool) LayoutPlan {
	z := int32(22)
	if !farApart {
		z = 3
	}
	spine := []SpineSegment{
		{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 40, Z: 0}},
		{From: domain.Cell{X: 0, Z: -30}, To: domain.Cell{X: 0, Z: 30}},
		{From: domain.Cell{X: 40, Z: -30}, To: domain.Cell{X: 40, Z: 30}},
	}
	p := LayoutPlan{Spine: spine, Rooms: []LayoutRoom{
		{Role: ModuleKitchen, Interior: Rectangle{X: 3, Z: z, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: z + 2}, DoorRot: domain.West},
		{Role: ModuleFreezer, Interior: Rectangle{X: 33, Z: z, Width: 5, Height: 5}, Door: domain.Cell{X: 38, Z: z + 2}, DoorRot: domain.East},
	}}
	if farApart {
		// More kitchens up the same crossing, each walking to the freezer.
		for _, kz := range []int32{16, 10} {
			p.Rooms = append(p.Rooms, LayoutRoom{Role: ModuleKitchen, Interior: Rectangle{X: 3, Z: kz, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: kz + 2}, DoorRot: domain.West})
		}
	}
	p.Entrances = hallEntrances(spine)
	return p
}

func TestHallEntrancesAreTheFreeEnds(t *testing.T) {
	p := ringPlan(true)
	ends := freeEnds(p.Spine)
	// The main hallway's ends lie inside the crossings' bands: only the
	// crossings' four ends reach out of the base.
	if len(ends) != 4 {
		t.Fatal("free ends", ends)
	}
	if len(p.Entrances) != 12 {
		t.Fatal("entrances", len(p.Entrances))
	}
	if !(LayoutPlan{Spine: p.Spine, Entrances: p.Entrances, Rooms: p.Rooms}).Valid() {
		t.Fatal("invalid")
	}
}

func TestRouteRingsKeepsARingOnlyWhereItShortensAWalk(t *testing.T) {
	g := openGround(-10, -40, 70, 100)
	p := ringPlan(true)
	before := planWalk(p)
	ringed := g.routeRings(p)
	if len(ringed.Spine) <= len(p.Spine) {
		t.Fatal("no ring joins the far crossings")
	}
	if after := planWalk(ringed); after >= before {
		t.Fatal("the ring does not shorten the walk", before, after)
	}
	if _, err := CheckRoutes(ringed); err != nil {
		t.Fatal(err)
	}
	if !ringed.Valid() {
		t.Fatal("invalid ring plan")
	}
	// The same H with both rooms beside the main hallway gains nothing.
	near := ringPlan(false)
	if got := g.routeRings(near); len(got.Spine) != len(near.Spine) {
		t.Fatal("a ring that shortens no walk", got.Spine)
	}
}

func TestRouteRingsKeepsOffRoomsAndObstacles(t *testing.T) {
	g := openGround(-10, -40, 70, 100)
	// A rich patch over the straight way across the north end.
	for _, c := range rectCells(Rectangle{X: 18, Z: 29, Width: 4, Height: 3}) {
		delete(g.core, c)
	}
	p := g.routeRings(ringPlan(true))
	if len(p.Spine) <= 3 {
		t.Fatal("the ring should go around the patch")
	}
	bands := hallBands(p.Spine[3:])
	for c := range bands {
		if !g.core[c] && c.X >= 18 && c.X < 22 && c.Z >= 29 && c.Z < 32 {
			t.Fatal("ring hallway on the obstacle", c)
		}
	}
	for _, r := range p.Rooms {
		for _, c := range rectCells(roomWalls(r)) {
			if bands[c] {
				t.Fatal("ring hallway through a room", r.Role, c)
			}
		}
	}
}

func TestAddSecondDoorsOnlyForPassThroughRooms(t *testing.T) {
	for _, role := range []ModuleRole{ModuleDining, ModuleBedroom} {
		p := bridgedHallways(role)
		p.Rooms[0].Doors = nil
		got := p.addSecondDoors(nil)
		doors := len(got.Rooms[0].Doors)
		if role == ModuleDining && doors != 1 {
			t.Fatal("dining touches two hallways and should take a second door", doors)
		}
		if role == ModuleBedroom && doors != 0 {
			t.Fatal("a bedroom never takes one", doors)
		}
		if role == ModuleDining {
			if _, err := CheckRoutes(got); err != nil {
				t.Fatal(err)
			}
			if side, ok := doorSide(got.Rooms[0].Interior, got.Rooms[0].Doors[0].Cell); !ok || side != got.Rooms[0].Doors[0].Rot {
				t.Fatal("door rotation", got.Rooms[0].Doors[0])
			}
		}
	}
}

// housingFixture sites a base for pawns colonists on bare ground.
func housingFixture(t *testing.T, pawns int, suites ...float64) (LayoutPlan, MapSurvey) {
	t.Helper()
	s := zoningSurvey(300, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 0.5} })
	zones := Zone(s)
	g := newCoreGrid(zones, nil).withSoil(s)
	seed, ok := g.seed()
	if !ok {
		t.Fatal("no seed")
	}
	return g.generate(LayoutPlan{Zones: zones}, seed, pawns, 1, BuildTierCamp, suites...), s
}

func TestGenerateHundredColonistsGetTenFullWings(t *testing.T) {
	plan, s := housingFixture(t, 100)
	if !plan.Valid() {
		t.Fatal("invalid")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	if len(plan.Wings) != 10 {
		t.Fatal("wings", len(plan.Wings))
	}
	var reserves []Rectangle
	for _, w := range plan.Wings {
		if len(w.Rooms) != wingMaxRooms {
			t.Fatal("a wing short of full size", len(w.Rooms))
		}
		reserves = append(reserves, wingReserve(w))
	}
	for i := range reserves {
		for j := i + 1; j < len(reserves); j++ {
			if rectsOverlap(reserves[i], reserves[j]) {
				t.Fatal("wings overlap", reserves[i], reserves[j])
			}
		}
		for _, r := range plan.Rooms {
			if rectsOverlap(reserves[i], roomWalls(r)) {
				t.Fatal("a wing over a room", r.Role)
			}
		}
	}
	if plan.LayoutOutgrown(100) {
		t.Fatal("outgrown")
	}
	// Every bedroom's door opens on its wing's corridor, which hangs off a
	// hallway: the bedroom to dining trips all have a route.
	if sc := Score(plan, s); len(sc.Missing) != 0 || sc.RoutesErr != "" {
		t.Fatal(sc)
	}
}

func TestGenerateSuiteBlocksAreCappedAndReachable(t *testing.T) {
	targets := make([]float64, 14)
	for i := range targets {
		targets[i] = 60 + float64(i)
	}
	plan, _ := housingFixture(t, 4, targets...)
	suites, blocks := 0, 0
	for _, w := range plan.Wings {
		if w.Purpose != WingSuites {
			continue
		}
		blocks++
		suites += len(w.Rooms)
		if len(w.Rooms) > suiteMaxRooms {
			t.Fatal("suite block over the cap", len(w.Rooms))
		}
	}
	if suites != len(targets) || blocks < 3 {
		t.Fatal("suites", suites, "blocks", blocks)
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	if !plan.Valid() {
		t.Fatal("invalid")
	}
}

func TestSiteCoreCourtyardNetworkKeepsOffThePatch(t *testing.T) {
	s := courtyardSurvey()
	plan := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, BuildTierCamp)
	if !plan.Valid() {
		t.Fatal("invalid")
	}
	if _, err := CheckRoutes(plan); err != nil {
		t.Fatal(err)
	}
	patch := Rectangle{X: 44, Z: 44, Width: 12, Height: 12}
	for _, h := range spineRects(plan.Hallways()) {
		if rectsOverlap(h, patch) {
			t.Fatal("hallway on the patch", h)
		}
	}
	if again := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, BuildTierCamp); !reflect.DeepEqual(plan, again) {
		t.Fatal("siting is not deterministic")
	}
}

func TestGenerateIsDeterministic(t *testing.T) {
	a, _ := housingFixture(t, 30)
	b, _ := housingFixture(t, 30)
	if !reflect.DeepEqual(a, b) {
		t.Fatal("two runs differ")
	}
}
