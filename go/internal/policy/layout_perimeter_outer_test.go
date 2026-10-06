package policy

import (
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// outerPlan plans a core with a rich patch of the given width north of it, a
// pen and a geothermal enclosure south, on bare ground.
func outerPlan(t *testing.T, width int32) (p LayoutPlan, units map[domain.Cell]bool) {
	t.Helper()
	ground := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} }
	plan := corePlan(Zone(zoningSurvey(200, ground)), 3, BuildTierCamp)
	core := footprintBox(plan, 200, 200)
	// The patch lies north of the core, the geothermal enclosure south.
	end := core.Z - perimeterGap - perimeterThick - 12
	x0 := core.X + core.Width/2 - width/2
	units = map[domain.Cell]bool{}
	zone := LayoutZone{Kind: ZoneField}
	for z := end - 20; z < end; z++ {
		zone.Runs = append(zone.Runs, RowRun{Z: z, X: x0, Length: width})
		for x := x0; x < x0+width; x++ {
			units[domain.Cell{X: x, Z: z}] = true
		}
	}
	plan.Zones = append(plan.Zones, zone)
	south := core.Z + core.Height + perimeterGap + perimeterThick + 12
	geo := Rectangle{X: core.X + 30, Z: south, Width: 6, Height: 6}
	plan.Reservations = append(plan.Reservations, LayoutReservation{Kind: ReserveGeothermal, Area: geo})
	for _, r := range []Rectangle{geo} {
		for _, c := range rectCells(r) {
			units[c] = true
		}
	}
	s := zoningSurvey(200, func(x, z int32) SurveyCell {
		c := ground(x, z)
		if units[domain.Cell{X: x, Z: z}] {
			c.Fertility = 1.4
		}
		return c
	})
	p = PlanPerimeter(plan, s)
	if !p.Valid() {
		t.Fatal("invalid plan")
	}
	return p, units
}

// outerReach is the cells reachable from the map corner past walls.
func outerReach(walls map[domain.Cell]bool) map[domain.Cell]bool {
	seen := map[domain.Cell]bool{{}: true}
	queue := []domain.Cell{{}}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, d := range [4]domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}} {
			n := addCell(c, d)
			if n.X >= 0 && n.Z >= 0 && n.X < 200 && n.Z < 200 && !walls[n] && !seen[n] {
				seen[n] = true
				queue = append(queue, n)
			}
		}
	}
	return seen
}

func TestOuterRingEnclosesGeothermal(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	p, _ := outerPlan(t, 40)
	walls, gates := reservedCells(p, ReserveOuterWall), reserved(p, ReserveOuterGate)
	if len(walls) == 0 || len(gates) == 0 {
		t.Fatal("outer walls", len(walls), "gates", gates)
	}
	for _, g := range gates {
		if g.Width*g.Height != perimeterThick {
			t.Fatal("outer gate spans the wall", g)
		}
		if !walls[domain.Cell{X: g.X, Z: g.Z}] || !walls[domain.Cell{X: g.X + g.Width - 1, Z: g.Z + g.Height - 1}] {
			t.Fatal("outer gate off the wall", g)
		}
	}
	seen := outerReach(walls)
	for _, r := range p.Reservations {
		if r.Kind != ReserveGeothermal {
			continue
		}
		for _, c := range rectCells(r.Area) {
			if seen[c] {
				t.Fatal("outer ring leaves the geothermal enclosure", c, "reachable")
			}
		}
	}
	// Apart from the core ring, the killbox and its approach by a free cell.
	apart := map[domain.Cell]bool{}
	for _, k := range []ReservationKind{ReservePerimeter, ReservePerimeterLight, ReserveBridge, ReservePerimeterGap, ReserveGate, ReserveKillbox, ReserveKillboxApproach} {
		for c := range reservedCells(p, k) {
			apart[c] = true
		}
	}
	if len(apart) == 0 {
		t.Fatal("no core ring")
	}
	for c := range walls {
		for _, d := range append(neighbours8[:], domain.Cell{}) {
			if apart[addCell(c, d)] {
				t.Fatal("outer wall", c, "touches the core ring or killbox")
			}
		}
	}
}

// A fertile patch is open ground, left outside the outer ring at any size;
// the geothermal enclosure beside it is still walled.
func TestOuterRingLeavesPatchOutside(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, width := range []int32{40, 126} {
		p, units := outerPlan(t, width)
		walls := reservedCells(p, ReserveOuterWall)
		if len(walls) == 0 {
			t.Fatal("no outer ring for the geothermal enclosure")
		}
		seen := outerReach(walls)
		outside := 0
		for c := range units {
			if seen[c] {
				outside++
			}
		}
		if want := int(width) * 20; outside < want {
			t.Fatal("the patch is walled in at width", width, ":", outside, "of", want, "cells outside")
		}
	}
}

func TestOuterRingNeedsUnits(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} })
	if len(reserved(p, ReserveOuterWall)) != 0 || len(reserved(p, ReserveOuterGate)) != 0 {
		t.Fatal("outer ring without a geothermal enclosure")
	}
}

// A derived plan on open fertile ground reserves a turbine pair; the core
// ring encloses the pair and its lanes whole, and none of them lies on the
// ring or across the killbox and its approaches.
func TestCoreRingEnclosesTurbinePair(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, pawns := range []int{3, 6, 10} {
		t.Run(strconv.Itoa(pawns), func(t *testing.T) { turbinePairInsideCoreRing(t, pawns) })
	}
}

func turbinePairInsideCoreRing(t *testing.T, pawns int) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, pawns, BuildTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	ring := coreRingBox(t, plan)
	core := map[domain.Cell]bool{}
	for _, k := range []ReservationKind{ReservePerimeter, ReservePerimeterLight, ReserveBridge, ReservePerimeterGap, ReserveGate, ReserveKillbox, ReserveKillboxApproach} {
		for c := range reservedCells(plan, k) {
			core[c] = true
		}
	}
	n := 0
	for _, r := range plan.Reservations {
		if r.Kind != ReserveTurbine && r.Kind != ReserveTurbineLane {
			continue
		}
		n++
		for _, c := range rectCells(r.Area) {
			if !contains(ring, c) {
				t.Fatal(r.Kind, "outside the core ring at", c)
			}
			if core[c] {
				t.Fatal(r.Kind, "on the core ring or killbox at", c)
			}
		}
	}
	if n != 5 {
		t.Fatal("turbine reservations", n)
	}
}

// The pen, barn and vet room stand inside the core ring.
func TestCoreRingEnclosesBarnAndVetRoom(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	ring := coreRingBox(t, plan)
	n := 0
	for _, r := range plan.Reservations {
		if r.Kind != ReserveBarn && r.Kind != ReserveVetRoom {
			continue
		}
		n++
		for _, c := range rectCells(r.Area) {
			if !contains(ring, c) {
				t.Fatal(r.Kind, "outside the core ring at", c)
			}
		}
	}
	if n < 2 {
		t.Fatal("barn and vet room reservations", n)
	}
}

// coreRingBox is the box the plan's core ring (wall and gate cells) spans.
func coreRingBox(t *testing.T, plan LayoutPlan) Rectangle {
	t.Helper()
	var box Rectangle
	for _, k := range []ReservationKind{ReservePerimeter, ReservePerimeterLight, ReserveBridge, ReservePerimeterGap} {
		for c := range reservedCells(plan, k) {
			box = unionRect(box, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
		}
	}
	if box.Width == 0 {
		t.Fatal("no core ring")
	}
	return box
}

// A geothermal the core ring takes in, beyond the killbox yard the core
// itself keeps, stands hard against the wall on its open sides.
func TestCoreRingHugsTakenInGeothermal(t *testing.T) {
	ground := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} }
	plan := corePlan(Zone(zoningSurvey(200, ground)), 3, BuildTierCamp)
	core := footprintBox(plan, 200, 200)
	geo := Rectangle{X: core.X + 30, Z: core.Z + core.Height + perimeterGap + 5, Width: 6, Height: 6}
	plan.Reservations = append(plan.Reservations, LayoutReservation{Kind: ReserveGeothermal, Area: geo})
	if !coreTakesIn(plan, 200, 200, plan.Reservations[len(plan.Reservations)-1]) {
		t.Fatal("geothermal not taken in")
	}
	p := PlanPerimeter(plan, zoningSurvey(200, ground))
	if !p.Valid() {
		t.Fatal("invalid plan")
	}
	walls := map[domain.Cell]bool{}
	for _, k := range []ReservationKind{ReservePerimeter, ReservePerimeterLight, ReserveBridge, ReservePerimeterGap, ReserveGate} {
		for c := range reservedCells(p, k) {
			walls[c] = true
		}
	}
	midX, midZ := geo.X+geo.Width/2, geo.Z+geo.Height/2
	for name, probe := range map[string]struct{ start, step domain.Cell }{
		"west":  {domain.Cell{X: geo.X - 1, Z: midZ}, domain.Cell{X: -1}},
		"east":  {domain.Cell{X: geo.X + geo.Width, Z: midZ}, domain.Cell{X: 1}},
		"south": {domain.Cell{X: midX, Z: geo.Z + geo.Height}, domain.Cell{Z: 1}},
	} {
		free := int32(0)
		for c := probe.start; !walls[c] && free <= perimeterGap; c = addCell(c, probe.step) {
			free++
		}
		if free > perimeterOuterYard {
			t.Error(name, "wall after", free, "free cells, want <=", perimeterOuterYard)
		}
	}
}
