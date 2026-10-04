package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func coreTestZones() []LayoutZone {
	// Soil everywhere, a rock block in the east third.
	return Zone(zoningSurvey(120, func(x, z int32) SurveyCell {
		if x >= 80 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}))
}

// roomRect is a room with its walls.
func roomRect(r LayoutRoom) Rectangle {
	in := r.Interior
	return Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}
}

func checkCore(t *testing.T, p LayoutPlan, pawns int) {
	t.Helper()
	if len(p.Spine) == 0 || !p.Valid() {
		t.Fatalf("plan %+v", p)
	}
	halls := spineRects(p.Hallways())
	hall := func(c domain.Cell) bool {
		for _, h := range halls {
			if rectsOverlap(h, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}) {
				return true
			}
		}
		return false
	}
	count := map[ModuleRole]int{}
	rooms := p.AllRooms()
	for i, a := range rooms {
		count[a.Role]++
		// The door is in the wall and opens on a hallway.
		step := map[domain.Rotation]domain.Cell{domain.North: {Z: 1}, domain.South: {Z: -1}, domain.East: {X: 1}, domain.West: {X: -1}}[a.DoorRot]
		if !hall(domain.Cell{X: a.Door.X + step.X, Z: a.Door.Z + step.Z}) && (a.Link == nil || *a.Link != a.Door) {
			t.Fatal("room off the spine", a)
		}
		for j, b := range rooms {
			if i == j {
				continue
			}
			ra, rb := a.Interior, roomRect(b)
			// Interiors never overlap another room's walls or floor.
			if ra.X < rb.X+rb.Width && rb.X < ra.X+ra.Width && ra.Z < rb.Z+rb.Height && rb.Z < ra.Z+ra.Height {
				t.Fatal("overlap", a, b)
			}
		}
	}
	if want := (pawns + wingMaxRooms - 1) / wingMaxRooms * wingMaxRooms; count[ModuleBedroom] != want {
		t.Fatal("bedrooms", count)
	}
	for _, role := range coreBaseRooms {
		if count[role] != 1 && (role != ModuleTomb || count[role] < 1) {
			t.Fatal("missing", role)
		}
	}
}

func TestPlanCore(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	checkCore(t, p, 3)
}

func TestGrowKeepsRooms(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	g := growPlan(p, 12, 1, BuildTierCamp)
	checkCore(t, g, 12)
	for i, r := range p.Rooms {
		if !g.Rooms[i].Same(r) {
			t.Fatal("moved", r, g.Rooms[i])
		}
	}
	dug := false
	for _, r := range g.Rooms {
		dug = dug || r.Dug
	}
	if !dug {
		t.Fatal("no room reached the rock")
	}
}

func TestGrowStopsAtEdge(t *testing.T) {
	g := corePlan(coreTestZones(), 500, BuildTierCamp)
	if n := len(g.AllRooms()); n < 20 || n > 500 {
		t.Fatal("rooms", len(g.AllRooms()))
	}
	for _, r := range g.Rooms {
		if r.Interior.X < LayoutEdgeMargin || r.Interior.X+r.Interior.Width > 120-LayoutEdgeMargin {
			t.Fatal("off the core", r)
		}
	}
}

// The freezer shares a wall with the kitchen: its hallway door takes the
// haulers, its link door the cook (#819).
func TestFreezerLinksToTheKitchen(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	var kitchen, freezer LayoutRoom
	for _, r := range p.Rooms {
		switch r.Role {
		case ModuleKitchen:
			kitchen = r
		case ModuleFreezer:
			freezer = r
		}
	}
	k, f := kitchen.Interior, freezer.Interior
	if freezer.Link == nil {
		t.Fatalf("freezer %+v has no kitchen door", freezer)
	}
	l := *freezer.Link
	inside := func(r Rectangle, c domain.Cell) bool {
		return c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height
	}
	for _, step := range []int32{-1, 1} {
		a, b := domain.Cell{X: l.X - step, Z: l.Z}, domain.Cell{X: l.X + step, Z: l.Z}
		if inside(k, a) && inside(f, b) {
			if _, err := CheckRoutes(p); err != nil {
				t.Fatal(err)
			}
			return
		}
	}
	t.Fatalf("link %v is not in the wall between kitchen %+v and freezer %+v", l, k, f)
}

// The dining room takes the freezer's free side wall with a door into it
// (#936), so the meal stockpile can sit in the cold one door from the
// table; no meal closet is planned then.
func TestDiningOpensIntoTheFreezer(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	var dining LayoutRoom
	for _, r := range p.Rooms {
		if r.Role == ModuleDining {
			dining = r
		}
		if r.Role == ModuleMealCloset {
			t.Fatal("closet planned beside a freezer door", r)
		}
	}
	if dining.Link == nil || !p.FreezerOpensInto(dining) {
		t.Fatalf("dining %+v has no door into the freezer", dining)
	}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
}

// A dining room with no freezer door (a plan from before #936) gets a 2x2
// meal closet behind its back wall, its door in that wall and its cooler
// site in the closet's own back wall, venting away from the dining room.
func TestMealClosetBehindTheDiningRoom(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	for i := range p.Rooms {
		if p.Rooms[i].Role == ModuleDining {
			p.Rooms[i].Link = nil
		}
	}
	grown := growPlan(p, 3, 1, BuildTierCamp)
	var dining, closet LayoutRoom
	for _, r := range grown.Rooms {
		switch r.Role {
		case ModuleDining:
			dining = r
		case ModuleMealCloset:
			closet = r
		}
	}
	if closet.Role == "" || closet.Interior.Width != 2 || closet.Interior.Height != 2 {
		t.Fatalf("closet %+v", closet)
	}
	d := dining.Interior
	if !inWall(d, closet.Door) || !inWall(closet.Interior, closet.Door) || closet.DoorRot != dining.DoorRot {
		t.Fatalf("closet door %v not in the wall shared with dining %+v", closet.Door, d)
	}
	back := d.Z + d.Height
	if dining.DoorRot == domain.North {
		back = d.Z - 1
	}
	if closet.Door.Z != back {
		t.Fatalf("closet %+v is not behind the dining room's back wall %d", closet, back)
	}
	if again := growPlan(grown, 3, 1, BuildTierCamp); len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("second closet planned")
	}
	withExhaust := PlanUtilities(grown, UtilityWants{})
	site, _, ok := withExhaust.CoolerExhaust(closet)
	if !ok || !inWall(closet.Interior, site.Cell) || inWall(d, site.Cell) {
		t.Fatalf("closet cooler site %+v %v", site, ok)
	}
}

// A fresh core reserves its centre crossing: the main hallway grows out
// from it on both sides and no room takes its column (#952).
func TestCoreReservesCentreCrossing(t *testing.T) {
	p := corePlan(coreTestZones(), 3, BuildTierCamp)
	if len(p.Spine) != 2 || alongX(p.Spine[1]) {
		t.Fatal("spine", p.Spine)
	}
	main, cx := p.Spine[0], p.Spine[1].From.X
	if west, east := cx-main.From.X, main.To.X-cx; west < 10 || east < 10 {
		t.Fatal("lopsided about the crossing", main, cx)
	}
	for _, r := range p.Rooms {
		w := roomWalls(r)
		if w.X <= cx+SpineWidth/2 && cx-SpineWidth/2 < w.X+w.Width {
			t.Fatal("room on the crossing's column", r)
		}
	}
}

// A growing core fills the main hallway, then its crossings, each on
// both sides of the main hallway, never one (no L or U); rooms never move
// and every door opens on a hallway (#952).
func TestGrowBranchesIntoCrossings(t *testing.T) {
	zones := coreTestZones()
	p := corePlan(zones, 3, BuildTierCamp)
	// Bedrooms live in the wing (#1213); tomb rooms fill the hallways.
	for _, tombs := range []int{10, 20, 30} {
		g := growPlan(p, 3, tombs, BuildTierCamp)
		for i, r := range p.Rooms {
			if !g.Rooms[i].Same(r) {
				t.Fatal("moved", r, g.Rooms[i])
			}
		}
		if _, err := CheckRoutes(g); err != nil {
			t.Fatal(tombs, err)
		}
		checkCore(t, g, 3)
		p = g
	}
	if len(p.Spine) < 3 {
		t.Fatal("no end crossing", p.Spine)
	}
	z0 := p.Spine[0].From.Z
	for _, s := range p.Spine[1:] {
		north, south := false, false
		for _, r := range p.Rooms {
			if onSegment(r, s) {
				north = north || r.Interior.Z > z0
				south = south || r.Interior.Z < z0
			}
		}
		if lo, hi := min(s.From.Z, s.To.Z), max(s.From.Z, s.To.Z); lo > z0-SpineWidth || hi < z0+SpineWidth {
			t.Fatal("crossing runs out one side only", s)
		}
		if !north || !south {
			t.Fatal("crossing rooms on one side only", s, north, south)
		}
	}
}

// Dining sits nearer the core's centre than the tomb and battery room (#1535).
func TestDiningCentralTombAndBatteryAtTheFringe(t *testing.T) {
	for _, rock := range []int32{50, 80} {
		zones := Zone(zoningSurvey(120, func(x, z int32) SurveyCell {
			if x >= rock {
				return SurveyCell{Rock: true}
			}
			return SurveyCell{Walkable: true, Fertility: 1}
		}))
		centre, _ := newCoreGrid(zones, nil).seed()
		for _, tier := range []BuildTier{BuildTierCamp, BuildTierPowered} {
			for _, pawns := range []int{1, 4, 8} {
				p := PlanUtilities(growPlan(LayoutPlan{Zones: zones}, pawns, 1, tier), UtilityWants{})
				dist := map[ModuleRole]int32{}
				for _, r := range p.Rooms {
					dx := r.Interior.X + r.Interior.Width/2 - centre.X
					dz := r.Interior.Z + r.Interior.Height/2 - centre.Z
					dist[r.Role] = max(dx, -dx) + max(dz, -dz)
				}
				for _, far := range []ModuleRole{ModuleTomb, ModuleBattery} {
					if _, ok := dist[far]; !ok {
						t.Fatal(rock, "no", far, "room")
					}
					if dist[ModuleDining] >= dist[far] {
						t.Errorf("rock %d tier %v pawns %d: dining %d from the centre, %s %d", rock, tier, pawns, dist[ModuleDining], far, dist[far])
					}
				}
			}
		}
	}
}
