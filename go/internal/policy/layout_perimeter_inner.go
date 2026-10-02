package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The inner wall and the walls that cut the ring (#1584). The outer wall is
// the stone ring of PlanPerimeter, the edge of the home zone. Inside it a
// second 3-thick ring, wood to start, closes in the core's buildings only,
// never a field; 3-thick door walls run from it out to the outer ring and
// split the ground between. Each wall has an airlock gate, like the outer
// ring's.

const (
	// ReserveInnerWall marks inner ring and cross wall cells: a wooden
	// wall.
	ReserveInnerWall ReservationKind = "inner_wall"
	// ReserveInnerGate marks a gate through an inner or cross wall.
	ReserveInnerGate ReservationKind = "inner_gate"

	// innerYardWanted is the free ground between the core's walls and the
	// inner ring: ranged combat distance, so a defender holding the ring is
	// covered by rifles from the buildings. The ring comes in as far as it
	// must to stand inside the outer one.
	innerYardWanted int32 = 12
)

// innerWalls plans the inner ring and the cross walls around core. free
// says a cell can take a wall: inside the outer ring, open ground, off the
// killbox and every other reservation, and when strict off the fields. The
// ring takes field cells it must; a cross wall keeps off them where it can.
func innerWalls(core Rectangle, inside func(domain.Cell) bool, free func(domain.Cell, bool) bool) (map[domain.Cell]bool, []Rectangle) {
	best, lost := innerYardWanted, -1
	for yard := innerYardWanted; yard >= 1 && lost != 0; yard-- {
		n := 0
		for _, c := range rectCells(pad(core, yard+perimeterThick)) {
			if !contains(pad(core, yard), c) && !inside(c) {
				n++
			}
		}
		if lost < 0 || n < lost {
			best, lost = yard, n
		}
	}
	return innerWallsAt(core, best, inside, free)
}

func innerWallsAt(core Rectangle, innerYard int32, inside func(domain.Cell) bool, free func(domain.Cell, bool) bool) (walls map[domain.Cell]bool, gates []Rectangle) {
	walls = map[domain.Cell]bool{}
	outerEdge := pad(core, innerYard+perimeterThick)
	hole := pad(core, innerYard)
	ring := map[domain.Cell]bool{}
	for _, c := range rectCells(outerEdge) {
		if !contains(hole, c) && inside(c) && free(c, false) {
			ring[c] = true
			walls[c] = true
		}
	}
	// place tries offsets from the side's middle for a gate across the
	// wall whose three cells (along the thickness) all stand.
	gateAt := func(thick func(off, t int32) domain.Cell, span int32, inWall map[domain.Cell]bool) {
		for d := int32(0); d <= span; d++ {
			for _, off := range []int32{d, -d} {
				ok := true
				for t := int32(0); t < perimeterThick; t++ {
					ok = ok && inWall[thick(off, t)]
				}
				if ok {
					gates = append(gates, rectOf(thick(off, 0), thick(off, perimeterThick-1)))
					return
				}
			}
		}
	}
	mid := domain.Cell{X: core.X + core.Width/2, Z: core.Z + core.Height/2}
	// A gate through each side of the ring, across its thickness.
	gateAt(func(off, t int32) domain.Cell { return domain.Cell{X: mid.X + off, Z: outerEdge.Z + t} }, core.Width/2, ring)
	gateAt(func(off, t int32) domain.Cell {
		return domain.Cell{X: mid.X + off, Z: outerEdge.Z + outerEdge.Height - 1 - t}
	}, core.Width/2, ring)
	gateAt(func(off, t int32) domain.Cell { return domain.Cell{X: outerEdge.X + t, Z: mid.Z + off} }, core.Height/2, ring)
	gateAt(func(off, t int32) domain.Cell {
		return domain.Cell{X: outerEdge.X + outerEdge.Width - 1 - t, Z: mid.Z + off}
	}, core.Height/2, ring)

	// Cross walls: from each side of the ring straight out to the outer
	// ring, at the first offset from the middle where every cell stands.
	type dir struct{ dx, dz int32 }
	for _, d := range []dir{{0, -1}, {0, 1}, {-1, 0}, {1, 0}} {
		along := mid.X
		span := core.Width / 2
		if d.dx != 0 {
			along, span = mid.Z, core.Height/2
		}
		// cell is the cell k steps out from the ring's edge, t across.
		cell := func(off, k, t int32) domain.Cell {
			switch {
			case d.dz < 0:
				return domain.Cell{X: along + off - 1 + t, Z: outerEdge.Z - 1 - k}
			case d.dz > 0:
				return domain.Cell{X: along + off - 1 + t, Z: outerEdge.Z + outerEdge.Height + k}
			case d.dx < 0:
				return domain.Cell{X: outerEdge.X - 1 - k, Z: along + off - 1 + t}
			}
			return domain.Cell{X: outerEdge.X + outerEdge.Width + k, Z: along + off - 1 + t}
		}
		placed := false
		// The wall starts clear of the ring gate in the side's middle, which
		// would otherwise open onto the wall's end; only a core too small for
		// that takes the middle.
		for _, from := range []int32{perimeterThick + 1, 0} {
			for _, strict := range []bool{true, false} {
				for dd := from; dd <= span && !placed; dd++ {
					for _, off := range []int32{dd, -dd} {
						var run []domain.Cell
						reached := false
						for k := int32(0); ; k++ {
							out := 0
							for t := int32(0); t < perimeterThick; t++ {
								if !inside(cell(off, k, t)) {
									out++
								}
							}
							if out == int(perimeterThick) {
								reached = true
								break
							}
							if out != 0 {
								break
							}
							clear := true
							for t := int32(0); t < perimeterThick; t++ {
								c := cell(off, k, t)
								clear = clear && free(c, strict) && !ring[c]
								run = append(run, c)
							}
							if !clear {
								break
							}
						}
						if !reached || len(run) == 0 {
							continue
						}
						placed = true
						for _, c := range run {
							walls[c] = true
						}
						// The gate halfway along, across the wall's thickness.
						k := int32(len(run)/int(perimeterThick)) / 2
						gates = append(gates, rectOf(cell(off, k, 0), cell(off, k, perimeterThick-1)))
						break
					}
				}
			}
		}
	}
	return walls, gates
}
