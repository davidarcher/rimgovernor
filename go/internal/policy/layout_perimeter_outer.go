package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The core ring also walls in the geothermal enclosures that crowd it; the
// outer ring that once walled the rest apart was removed.
const (
	// perimeterOuterGap is the free cells between the core ring (and the
	// killbox approach) and the outer ring.
	perimeterOuterGap int32 = 1
	// perimeterOuterYard is the margin kept between the outer ring and what
	// it encloses.
	perimeterOuterYard int32 = 1
)

// outerEnclosed are the reservation kinds the outer ring walls in whole: the geothermal
// enclosures.
var outerEnclosed = map[ReservationKind]bool{ReserveGeothermal: true}

// innerEnclosed are the reservations the core ring walls in with the rooms:
// the materials yard, the barns and vet rooms and the turbine pairs with their
// lanes stand beside the core, inside its wall, not out in the fields.
var innerEnclosed = map[ReservationKind]bool{ReserveYard: true, ReserveBarn: true, ReserveVetRoom: true, ReserveTurbine: true, ReserveTurbineLane: true}

// wallRuns walls side sd's open cells into walls, run by run between the
// positions skip reports (an opening, bare terrain, soft ground), and returns
// each run's gates on the perimeterGatePitch: a run shorter than the pitch
// (a step of a squared-off diagonal) gives step gates, kept by pitchGates.
// Raiders neither mine nor attack natural rock (#1592): a run is walled only
// on its open cells, and not behind a rock row that spans the column and both
// neighbours, which no raider can reach round. axes are the positions of
// hallways meeting the side, which the pitch lines up with (#952).
func wallRuns(sd ringSide, impassable func(domain.Cell) bool, walls map[domain.Cell]bool, axes []int32, skip func(p int32) bool) (gates []Rectangle, stepGates []stepGate) {
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
					stepGates = append(stepGates, stepGate{area: g, face: sd.base(p), out: domain.Cell{X: -sd.in.X, Z: -sd.in.Z}, along: sd.al})
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

// stepGate is a gate on a run shorter than the pitch: its outer-face cell, the
// way out from it and the way along the wall.
type stepGate struct {
	area             Rectangle
	face, out, along domain.Cell
}

// opensOut is whether a gate leads outside: the strip of three cells wide and
// three deep straight out from its door holds no wall and no rock. On a step
// of a squared-off diagonal the next step's wall stands in that strip, and the
// door would open into the jog.
func (g stepGate) opensOut(blocked func(domain.Cell) bool) bool {
	for k := int32(1); k <= 3; k++ {
		for a := int32(-1); a <= 1; a++ {
			if blocked(domain.Cell{X: g.face.X + g.out.X*k + g.along.X*a, Z: g.face.Z + g.out.Z*k + g.along.Z*a}) {
				return false
			}
		}
	}
	return true
}

// pitchGates adds to gates the step gates a pitch clear of every other that
// lead outside, so a staircase is gated about as often as a straight side
// (#1287) and never into a jog.
func pitchGates(gates []Rectangle, stepGates []stepGate, blocked func(domain.Cell) bool) []Rectangle {
	for _, step := range stepGates {
		g := step.area
		clear := step.opensOut(blocked)
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
// pen, barn, vet room and turbine pairs beside them, and any
// geothermal crowding that footprint. It follows the plan's real outline,
// not its bounding rectangle (#1945).
func coreFootprint(plan LayoutPlan, w, h int32) []bool {
	fp := coreBaseFootprint(plan, w, h)
	gap := yardGap(fp, w, h, plan.YardCells)
	for _, r := range plan.Reservations {
		if outerEnclosed[r.Kind] && crowdsCore(fp, w, h, r.Area, gap) {
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
	gap := yardGap(base, w, h, plan.YardCells)
	tight := make([]bool, len(base))
	for _, r := range plan.Reservations {
		if outerEnclosed[r.Kind] && crowdsCore(base, w, h, r.Area, gap) {
			markRect(tight, w, h, r.Area)
		}
	}
	m := LayoutEdgeMargin + perimeterThick
	yard := Rectangle{X: m, Z: m, Width: w - 2*m, Height: h - 2*m}
	grown := growRegion(base, w, h, gap)
	for i, v := range growRegion(tight, w, h, perimeterOuterYard) {
		grown[i] = grown[i] || v
	}
	return newEnclosure(closeRegion(grown, w, h, yard), w, h)
}

// outerClear is how far the outer ring needs a unit to stand off the core
// footprint: the core ring's yard, its thickness, the gap and the outer ring's thickness.
const outerClear = perimeterGap + perimeterThick + perimeterOuterGap + perimeterThick

// perimeterGapMax caps how far the herd grows the yard past perimeterGap.
const perimeterGapMax = perimeterGap + 12

// yardGap is the yard between the core footprint fp and the wall (#2232): the
// smallest gap from perimeterGap up whose ring of cells around fp holds
// grazing cells, capped at perimeterGapMax. grazing 0 keeps perimeterGap.
func yardGap(fp []bool, w, h int32, grazing int32) int32 {
	if grazing <= 0 {
		return perimeterGap
	}
	var count [perimeterGapMax + 1]int32
	for _, d := range chebyshevField(w, h, fp, perimeterGapMax) {
		if d >= 1 {
			count[d]++
		}
	}
	var held int32
	for g := int32(1); g <= perimeterGapMax; g++ {
		held += count[g]
		if g >= perimeterGap && held >= grazing {
			return g
		}
	}
	return perimeterGapMax
}

// crowdsCore reports whether area stands within outerClear (Chebyshev), widened by
// the yard (gap) a herd grew past perimeterGap, of the core footprint. The outer
// ring cannot wall such a unit whole, so the core ring takes it in.
func crowdsCore(fp []bool, w, h int32, area Rectangle, gap int32) bool {
	clear := outerClear + gap - perimeterGap
	for z := max(area.Z-clear, 0); z < min(area.Z+area.Height+clear, h); z++ {
		for x := max(area.X-clear, 0); x < min(area.X+area.Width+clear, w); x++ {
			if fp[z*w+x] {
				return true
			}
		}
	}
	return false
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
