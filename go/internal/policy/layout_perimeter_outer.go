package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The outer ring (#1595): a second stone wall, planned up front around the
// rich field patches, the animal pen, the geothermal enclosures and the
// turbine pairs (with their lanes) near the core, wholly apart from the core ring (perimeterOuterGap cells between
// them, never a shared wall). It has its own reservation kinds, so the core
// ring's logic and the builder tell them apart; it holds no killbox. It
// traces, snaps to rock and gates as the core ring does (wallRuns); soft
// ground on it is walled like any other cell.
const (
	ReserveOuterWall ReservationKind = "outer_wall"
	ReserveOuterGate ReservationKind = "outer_gate"

	// perimeterPatchMax is the largest rich patch the ring takes in: a bigger
	// one (a whole valley floor) would wall the map and is left outside
	// (#1582).
	perimeterPatchMax = 2500
	// perimeterPatchMin is the smallest rich patch worth walling: a lone
	// fertile tile or two would stretch the ring (and its extra walls) across
	// open ground for nothing.
	perimeterPatchMin = 30
	// perimeterOuterGap is the free cells between the core ring (and the
	// killbox approach) and the outer ring.
	perimeterOuterGap int32 = 1
	// perimeterOuterYard is the margin kept between the outer ring and what
	// it encloses.
	perimeterOuterYard int32 = 1
)

// planOuterRing returns the outer ring's reservations: walls and gates around
// the units within twice perimeterFieldReach of the core ring (the chain limit, so a
// string of patches cannot walk the wall across the map). core is the core
// ring's enclosure and approaches the killbox's lanes, both kept clear.
func planOuterRing(plan LayoutPlan, s MapSurvey, core enclosure, approaches []Rectangle, impassable func(domain.Cell) bool) []LayoutReservation {
	w, h := core.w, core.h
	chain := pad(core.bbox, 2*perimeterFieldReach)
	region := make([]bool, w*h)
	found := false
	// take adds a unit (a patch, the pen, an enclosure) when some cell of it
	// lies within the chain, whole.
	take := func(cells []domain.Cell) {
		near := false
		for _, c := range cells {
			near = near || contains(chain, c)
		}
		for _, c := range cells {
			if near && c.X >= 0 && c.Z >= 0 && c.X < w && c.Z < h {
				region[c.Z*w+c.X] = true
				found = true
			}
		}
	}
	rich := make([]bool, w*h)
	for _, z := range plan.Zones {
		if z.Kind != ZoneField {
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				if x >= 0 && r.Z >= 0 && x < w && r.Z < h {
					rich[r.Z*w+x] = true
				}
			}
		}
	}
	for _, c := range s.Cells {
		if c.Fertility <= zoneRichFertility && c.Cell.X >= 0 && c.Cell.Z >= 0 && c.Cell.X < w && c.Cell.Z < h {
			rich[c.Cell.Z*w+c.Cell.X] = false
		}
	}
	for _, comp := range components(w, h, func(i int32) bool { return rich[i] }) {
		if len(comp) > perimeterPatchMax || len(comp) < perimeterPatchMin {
			continue
		}
		cells := make([]domain.Cell, len(comp))
		for k, i := range comp {
			cells[k] = domain.Cell{X: i % w, Z: i / w}
		}
		take(cells)
	}
	pairs := map[int32][]domain.Cell{}
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePen, ReserveGeothermal:
			take(rectCells(r.Area))
		case ReserveTurbine, ReserveTurbineLane:
			// A turbine pair and its lanes stand whole inside the ring (#1597).
			pairs[r.Pair] = append(pairs[r.Pair], rectCells(r.Area)...)
		}
	}
	for _, cells := range pairs {
		take(cells)
	}
	if !found {
		return nil
	}

	// What the ring must keep clear of: the core and its ring, and the
	// approach lanes. The ring stands at least ring+gap cells off them, so
	// what it encloses stands a ring's thickness further.
	apart := make([]bool, w*h)
	copy(apart, core.in)
	for _, r := range approaches {
		for _, c := range rectCells(clipRect(r, Rectangle{Width: w, Height: h})) {
			apart[c.Z*w+c.X] = true
		}
	}
	ringOff := perimeterThick + perimeterOuterGap + 1
	near := chebyshevField(w, h, apart, ringOff+perimeterThick-1)
	m := LayoutEdgeMargin + perimeterThick
	in := encloseRegion(region, w, h, Rectangle{X: m, Z: m, Width: w - 2*m, Height: h - 2*m}, perimeterOuterYard)
	for i := range in {
		in[i] = in[i] && near[i] < 0
	}
	enc := newEnclosure(in, w, h)
	if enc.bbox.Width == 0 {
		return nil
	}
	for i := range enc.ring {
		enc.ring[i] = enc.ring[i] && (near[i] < 0 || near[i] >= ringOff)
	}

	sides, owner := enc.sides()
	walls := map[domain.Cell]bool{}
	var gates, stepGates []Rectangle
	for _, sd := range sides {
		g, sg := wallRuns(sd, impassable, walls, nil, func(p int32) bool {
			for t := int32(0); t < perimeterThick; t++ {
				if !impassable(sd.cell(p, t)) {
					return false
				}
			}
			return true
		})
		gates, stepGates = append(gates, g...), append(stepGates, sg...)
	}
	for _, c := range enc.ringCells() {
		if _, ok := owner[c]; !ok && !impassable(c) {
			walls[c] = true
		}
	}
	var res []LayoutReservation
	for _, r := range cellRects(walls) {
		res = append(res, LayoutReservation{Kind: ReserveOuterWall, Area: r})
	}
	for _, g := range pitchGates(gates, stepGates) {
		res = append(res, LayoutReservation{Kind: ReserveOuterGate, Area: g})
	}
	return res
}

// wallRuns walls side sd's open cells into walls, run by run between the
// positions skip reports (an opening, bare terrain, soft ground), and returns
// each run's gates on the perimeterGatePitch: a run shorter than the pitch
// (a step of a squared-off diagonal) gives step gates, kept by pitchGates.
// Raiders neither mine nor attack natural rock (#1592): a run is walled only
// on its open cells, and not behind a rock row that spans the column and both
// neighbours, which no raider can reach round. axes are the positions of
// hallways meeting the side, which the pitch lines up with (#952).
func wallRuns(sd ringSide, impassable func(domain.Cell) bool, walls map[domain.Cell]bool, axes []int32, skip func(p int32) bool) (gates, stepGates []Rectangle) {
	sealed := func(p, t int32) bool {
		for u := int32(0); u < t; u++ {
			if impassable(sd.cell(p-1, u)) && impassable(sd.cell(p, u)) && impassable(sd.cell(p+1, u)) {
				return true
			}
		}
		return false
	}
	openColumn := func(p int32) bool {
		for t := int32(0); t < perimeterThick; t++ {
			if impassable(sd.cell(p, t)) {
				return false
			}
		}
		return true
	}
	start := int32(-1)
	flush := func(end int32) {
		if start < 0 {
			return
		}
		for p := start; p <= end; p++ {
			for t := int32(0); t < perimeterThick; t++ {
				if c := sd.cell(p, t); !impassable(c) && !sealed(p, t) {
					walls[c] = true
				}
			}
		}
		n := end - start + 1
		if n >= 5 {
			first := start + min(perimeterGatePitch/2, n/2)
			for _, a := range axes {
				if a > start && a < end {
					first = start + (a-start)%perimeterGatePitch
					break
				}
			}
			for p := first; p <= end; p += perimeterGatePitch {
				if !openColumn(p) {
					continue
				}
				g := rectOf(sd.base(p), sd.cell(p, perimeterThick-1))
				if n < perimeterGatePitch {
					stepGates = append(stepGates, g)
				} else {
					gates = append(gates, g)
				}
			}
		}
		start = -1
	}
	for p := sd.lo; p <= sd.hi; p++ {
		if skip(p) {
			flush(p - 1)
		} else if start < 0 {
			start = p
		}
	}
	flush(sd.hi)
	return gates, stepGates
}

// pitchGates adds to gates the step gates a pitch clear of every other, so a
// staircase is gated about as often as a straight side (#1287).
func pitchGates(gates, stepGates []Rectangle) []Rectangle {
	for _, g := range stepGates {
		clear := true
		for _, o := range gates {
			clear = clear && max(o.X-g.X, g.X-o.X, o.Z-g.Z, g.Z-o.Z) >= perimeterGatePitch
		}
		if clear {
			gates = append(gates, g)
		}
	}
	return gates
}

// coreBox is the core ring's box: the rooms with their walls and the
// hallways.
func coreBox(plan LayoutPlan) Rectangle {
	var core Rectangle
	for _, r := range plan.AllRooms() {
		core = unionRect(core, pad(r.Interior, 1))
	}
	for _, sg := range plan.Hallways() {
		core = unionRect(core, pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	return core
}

// outerKeepOut marks the cells of a w x h map the core ring will occupy or
// crowd: the core's enclosure and everything the outer ring must stand
// clear of it. A unit sited off these is enclosed by the outer ring whole
// (#1597); PlanUtilities runs before PlanPerimeter, so the core's box
// stands in for the ring.
func outerKeepOut(plan LayoutPlan, w, h int32) []bool {
	out := make([]bool, w*h)
	core := coreBox(plan)
	if core.Width == 0 || w < 1 || h < 1 {
		return out
	}
	enc := planEnclosure(core, w, h)
	for i, d := range chebyshevField(w, h, enc.in, perimeterThick+perimeterOuterGap+perimeterThick) {
		out[i] = d >= 0
	}
	return out
}
