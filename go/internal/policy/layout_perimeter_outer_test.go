package policy

import (
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// outerPlan plans a core with a rich patch of the given width north of it, a
// pen and a geothermal enclosure south, on bare ground.
func outerPlan(t *testing.T, width int32) (p LayoutPlan, units map[domain.Cell]bool) {
	t.Helper()
	ground := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} }
	plan := PlanCore(Zone(zoningSurvey(200, ground)), 3, BuildTierCamp)
	var core Rectangle
	for _, r := range plan.AllRooms() {
		core = unionRect(core, pad(r.Interior, 1))
	}
	for _, sg := range plan.Hallways() {
		core = unionRect(core, pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	// The patch lies north of the core, the pen and the enclosure south.
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
	pen := Rectangle{X: core.X, Z: south, Width: 12, Height: 10}
	geo := Rectangle{X: core.X + 30, Z: south, Width: 6, Height: 6}
	plan.Reservations = append(plan.Reservations, LayoutReservation{Kind: ReservePen, Area: pen}, LayoutReservation{Kind: ReserveGeothermal, Area: geo})
	for _, r := range []Rectangle{pen, geo} {
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

func TestOuterRingEnclosesPatchPenAndGeothermal(t *testing.T) {
	p, units := outerPlan(t, 40)
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
	for c := range units {
		if seen[c] {
			t.Fatal("outer ring leaves", c, "reachable")
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

// A patch past the size cap is a valley floor, left outside; the pen and
// the geothermal enclosure beside it are still walled.
func TestOuterRingSkipsHugePatch(t *testing.T) {
	p, units := outerPlan(t, 126)
	walls := reservedCells(p, ReserveOuterWall)
	if len(walls) == 0 {
		t.Fatal("no outer ring for the pen")
	}
	seen := outerReach(walls)
	for _, r := range p.Reservations {
		if r.Kind == ReservePen || r.Kind == ReserveGeothermal {
			for _, c := range rectCells(r.Area) {
				if seen[c] {
					t.Fatal(r.Kind, "left outside", c)
				}
			}
		}
	}
	outside := 0
	for c := range units {
		if seen[c] {
			outside++
		}
	}
	if outside < 126*20 {
		t.Fatal("the huge patch is walled in:", outside, "cells outside")
	}
}

func TestOuterRingNeedsUnits(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true} })
	if len(reserved(p, ReserveOuterWall)) != 0 || len(reserved(p, ReserveOuterGate)) != 0 {
		t.Fatal("outer ring without a patch, pen or geothermal enclosure")
	}
}

// A derived plan on open fertile ground reserves a turbine pair; the outer
// ring encloses the pair and its lanes whole, and none of them lies on the
// core ring or across the killbox and its approaches (#1597).
func TestOuterRingEnclosesTurbinePair(t *testing.T) {
	for _, pawns := range []int{3, 6, 10} {
		t.Run(strconv.Itoa(pawns), func(t *testing.T) { turbinePairInsideOuterRing(t, pawns) })
	}
}

func turbinePairInsideOuterRing(t *testing.T, pawns int) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, pawns, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	walls := reservedCells(plan, ReserveOuterWall)
	if len(walls) == 0 {
		t.Fatal("no outer ring")
	}
	seen := outerReach(walls)
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
			if seen[c] {
				t.Fatal(r.Kind, "outside the outer ring at", c)
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

// The barn and vet room stand inside the outer ring with the pen (#1633).
func TestOuterRingEnclosesBarnAndVetRoom(t *testing.T) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	walls := reservedCells(plan, ReserveOuterWall)
	if len(walls) == 0 {
		t.Fatal("no outer ring")
	}
	seen := outerReach(walls)
	n := 0
	for _, r := range plan.Reservations {
		if r.Kind != ReserveBarn && r.Kind != ReserveVetRoom {
			continue
		}
		n++
		for _, c := range rectCells(r.Area) {
			if seen[c] {
				t.Fatal(r.Kind, "outside the outer ring at", c)
			}
		}
	}
	if n != 2 {
		t.Fatal("barn and vet room reservations", n)
	}
}
