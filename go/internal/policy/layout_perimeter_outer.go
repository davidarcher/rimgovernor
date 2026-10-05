package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The outer ring (#1595): a second stone wall, planned up front around the
// geothermal enclosures near the core, wholly apart from the core ring
// (perimeterOuterGap cells between them, never a shared wall). Fertile
// patches stand outside it: fields are open ground. It has its own reservation kinds, so the core
// ring's logic and the builder tell them apart; it holds no killbox. It
// traces, snaps to rock and gates as the core ring does (wallRuns); soft
// ground on it is walled like any other cell.
const (
	ReserveOuterWall ReservationKind = "outer_wall"
	ReserveOuterGate ReservationKind = "outer_gate"

	// perimeterOuterGap is the free cells between the core ring (and the
	// killbox approach) and the outer ring.
	perimeterOuterGap int32 = 1
	// perimeterOuterYard is the margin kept between the outer ring and what
	// it encloses.
	perimeterOuterYard int32 = 1
)

// outerEnclosed are the reservation kinds the outer ring walls in whole: the
// animal yards (a new yard kind, such as the barn, registers here) and the
// geothermal enclosures.
var outerEnclosed = map[ReservationKind]bool{ReserveGeothermal: true}

// innerEnclosed are the reservations the core ring walls in with the rooms:
// the animal yards and the turbine pairs with their lanes stand beside the
// core, inside its wall, not out in the fields.
var innerEnclosed = map[ReservationKind]bool{ReservePen: true, ReserveBarn: true, ReserveVetRoom: true, ReserveTurbine: true, ReserveTurbineLane: true}

// planOuterRing returns the outer ring's reservations: walls and gates around
// the units within twice perimeterFieldReach of the core ring. core is the core
// ring's enclosure and approaches the killbox's lanes, both kept clear.
func planOuterRing(plan LayoutPlan, s MapSurvey, core enclosure, approaches []Rectangle, impassable func(domain.Cell) bool) []LayoutReservation {
	w, h := core.w, core.h
	chain := pad(core.bbox, 2*perimeterFieldReach)
	region := make([]bool, w*h)
	found := false
	// take adds a unit (an enclosure) when some cell of it
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
	pairs := map[int32][]domain.Cell{}
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReserveTurbine, ReserveTurbineLane:
			// The core ring walls a turbine pair in; none stands outside.
		default:
			if outerEnclosed[r.Kind] && !coreTakesIn(plan, w, h, r) {
				take(rectCells(r.Area))
			}
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

// coreFootprint marks the cells of a w x h map the core ring walls in: each
// room's interior with its walls, each hallway padded by SpineWidth/2, the
// pen, barn, vet room and turbine pairs beside them, and any outer-ring unit
// crowding that footprint (coreTakesIn). It follows the plan's real outline,
// not its bounding rectangle (#1945).
func coreFootprint(plan LayoutPlan, w, h int32) []bool {
	fp := coreBaseFootprint(plan, w, h)
	for _, r := range plan.Reservations {
		if outerEnclosed[r.Kind] && crowdsCore(fp, w, h, r.Area) {
			markRect(fp, w, h, pad(r.Area, 1))
		}
	}
	return fp
}

// coreBaseFootprint is coreFootprint before any outer-ring unit is taken in.
func coreBaseFootprint(plan LayoutPlan, w, h int32) []bool {
	fp := make([]bool, max(w, 0)*max(h, 0))
	for _, r := range plan.AllRooms() {
		markRect(fp, w, h, pad(r.Interior, 1))
	}
	for _, sg := range plan.Hallways() {
		markRect(fp, w, h, pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	for _, r := range plan.Reservations {
		if innerEnclosed[r.Kind] {
			markRect(fp, w, h, pad(r.Area, 1))
		}
	}
	return fp
}

// markRect sets the cells of r that lie on the w x h map.
func markRect(fp []bool, w, h int32, r Rectangle) {
	for z := max(r.Z, 0); z < min(r.Z+r.Height, h); z++ {
		for x := max(r.X, 0); x < min(r.X+r.Width, w); x++ {
			fp[z*w+x] = true
		}
	}
}

// coreEnclosure traces the core ring's enclosure: the core grown by the
// killbox yard, plus each outer-ring unit it takes in (crowdsCore) grown only
// by perimeterOuterYard, as a blob of its own: a geothermal has no killbox lane
// to clear, so it stands hard against the wall.
func coreEnclosure(plan LayoutPlan, w, h int32) enclosure {
	base := coreBaseFootprint(plan, w, h)
	tight := make([]bool, len(base))
	for _, r := range plan.Reservations {
		if outerEnclosed[r.Kind] && crowdsCore(base, w, h, r.Area) {
			markRect(tight, w, h, r.Area)
		}
	}
	m := LayoutEdgeMargin + perimeterThick
	yard := Rectangle{X: m, Z: m, Width: w - 2*m, Height: h - 2*m}
	grown := growRegion(base, w, h, perimeterGap)
	for i, v := range growRegion(tight, w, h, perimeterOuterYard) {
		grown[i] = grown[i] || v
	}
	return newEnclosure(closeRegion(grown, w, h, yard), w, h)
}

// planEnclosureCells traces the enclosure around a core footprint on a w x h
// map: the footprint grown by the yard, closed so the outline has no notches
// narrower than the closing.
func planEnclosureCells(footprint []bool, w, h int32) enclosure {
	m := LayoutEdgeMargin + perimeterThick
	yard := Rectangle{X: m, Z: m, Width: w - 2*m, Height: h - 2*m}
	return newEnclosure(encloseRegion(footprint, w, h, yard, perimeterGap), w, h)
}

// outerClear is how far the outer ring needs a unit to stand off the core
// footprint: the core ring's yard, its thickness, the gap and the outer ring's thickness.
const outerClear = perimeterGap + perimeterThick + perimeterOuterGap + perimeterThick

// crowdsCore reports whether area stands within outerClear (Chebyshev) of
// the core footprint. The outer ring cannot wall such a unit whole, so the
// core ring takes it in.
func crowdsCore(fp []bool, w, h int32, area Rectangle) bool {
	for z := max(area.Z-outerClear, 0); z < min(area.Z+area.Height+outerClear, h); z++ {
		for x := max(area.X-outerClear, 0); x < min(area.X+area.Width+outerClear, w); x++ {
			if fp[z*w+x] {
				return true
			}
		}
	}
	return false
}

// coreTakesIn reports whether the core ring walls the outer-ring unit r in.
func coreTakesIn(plan LayoutPlan, w, h int32, r LayoutReservation) bool {
	return crowdsCore(coreBaseFootprint(plan, w, h), w, h, r.Area)
}

// outerKeepOut marks the cells of a w x h map the core ring will occupy or
// crowd: the core's enclosure and everything the outer ring must stand
// clear of it. A unit sited off these is enclosed by the outer ring whole
// (#1597); PlanUtilities runs before PlanPerimeter, so the core's
// footprint stands in for the ring.
func outerKeepOut(plan LayoutPlan, w, h int32) []bool {
	out := make([]bool, max(w, 0)*max(h, 0))
	if w < 1 || h < 1 || len(plan.AllRooms()) == 0 && len(plan.Hallways()) == 0 {
		return out
	}
	enc := coreEnclosure(plan, w, h)
	for i, d := range chebyshevField(w, h, enc.in, perimeterThick+perimeterOuterGap+perimeterThick) {
		out[i] = d >= 0
	}
	return out
}
