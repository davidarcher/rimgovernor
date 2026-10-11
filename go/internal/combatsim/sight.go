package combatsim

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// adjacent is GenAdj.AdjacentCells: N, E, S, W, SE, NE, NW, SW (x east, z north).
var adjacent = [8]domain.Cell{
	{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0},
	{X: 1, Z: -1}, {X: 1, Z: 1}, {X: -1, Z: 1}, {X: -1, Z: -1},
}

func add(a, b domain.Cell) domain.Cell { return domain.Cell{X: a.X + b.X, Z: a.Z + b.Z} }

func abs32(v int32) int {
	if v < 0 {
		return int(-v)
	}
	return int(v)
}

// angleFlat ports IntVec3.AngleFlat for the offset d: the compass angle in
// degrees in [0, 360) with north (+z) at 0 and east (+x) at 90; zero for no offset.
func angleFlat(d domain.Cell) float64 {
	if d.X == 0 && d.Z == 0 {
		return 0
	}
	a := math.Atan2(float64(d.X), float64(d.Z)) * 180 / math.Pi
	if a < 0 {
		a += 360
	}
	return a
}

// lineOfSight ports GenSight.LineOfSight(start, end, map, skipFirstCell,
// validator, halfXOffset, halfZOffset): a Bresenham walk from start toward
// end that fails on the first cell that cannot be seen over. The end cell
// itself is never tested.
func (g *grid) lineOfSight(start, end domain.Cell, skipFirst bool, halfX, halfZ int) bool {
	if !g.inBounds(start) || !g.inBounds(end) {
		return false
	}
	sideOnEqual := start.Z < end.Z
	if start.X != end.X {
		sideOnEqual = start.X < end.X
	}
	dx := abs32(end.X - start.X)
	dz := abs32(end.Z - start.Z)
	x, z := int(start.X), int(start.Z)
	n := 1 + dx + dz
	xInc, zInc := -1, -1
	if end.X > start.X {
		xInc = 1
	}
	if end.Z > start.Z {
		zInc = 1
	}
	dx *= 4
	dz *= 4
	dx += halfX * 2
	dz += halfZ * 2
	errv := dx/2 - dz/2
	for n > 1 {
		c := domain.Cell{X: int32(x), Z: int32(z)}
		if !skipFirst || c != start {
			if !g.canBeSeenOver(c) {
				return false
			}
		}
		if errv > 0 || (errv == 0 && sideOnEqual) {
			x += xInc
			errv -= dz
		} else {
			z += zInc
			errv += dx
		}
		n--
	}
	return true
}

// leanSources ports ShootLeanUtility.LeanShootingSourcesFromTo: the cells a
// shooter at shooterLoc may lean into to see targetPos, in the game's order,
// appended to out (which is truncated first).
func (g *grid) leanSources(shooterLoc, targetPos domain.Cell, out []domain.Cell) []domain.Cell {
	out = out[:0]
	angle := angleFlat(domain.Cell{X: targetPos.X - shooterLoc.X, Z: targetPos.Z - shooterLoc.Z})
	north := angle > 270 || angle < 90
	south := angle > 90 && angle < 270
	west := angle > 180
	east := angle < 180
	var blocked [8]bool
	for i := range blocked {
		blocked[i] = !g.canBeSeenOver(add(shooterLoc, adjacent[i]))
	}
	if !blocked[1] && ((blocked[0] && !blocked[5] && north) || (blocked[2] && !blocked[4] && south)) {
		out = append(out, add(shooterLoc, domain.Cell{X: 1}))
	}
	if !blocked[3] && ((blocked[0] && !blocked[6] && north) || (blocked[2] && !blocked[7] && south)) {
		out = append(out, add(shooterLoc, domain.Cell{X: -1}))
	}
	if !blocked[2] && ((blocked[3] && !blocked[7] && west) || (blocked[1] && !blocked[4] && east)) {
		out = append(out, add(shooterLoc, domain.Cell{Z: -1}))
	}
	if !blocked[0] && ((blocked[3] && !blocked[6] && west) || (blocked[1] && !blocked[5] && east)) {
		out = append(out, add(shooterLoc, domain.Cell{Z: 1}))
	}
	if g.canBeSeenOver(shooterLoc) {
		out = append(out, shooterLoc)
	}
	for j := 0; j < 4; j++ {
		if blocked[j] {
			continue
		}
		toward := [4]bool{north, east, south, west}[j]
		if !toward {
			continue
		}
		if c := add(shooterLoc, adjacent[j]); g.hasCover(c) {
			out = append(out, c)
		}
	}
	return out
}

// CellCanSeeCell ports ShootLeanUtility.CellCanSeeCell: whether a pawn
// standing on source can shoot at one standing on dest, each side leaning
// around corners. It is the game's line-of-fire test between cells; it is
// symmetric except where the game's own asymmetries are (the port keeps them).
func (s *Sim) CellCanSeeCell(source, dest domain.Cell) bool {
	g := &s.g
	if !g.inBounds(source) || !g.inBounds(dest) {
		return false
	}
	if !g.canBeSeenOver(source) || !g.canBeSeenOver(dest) {
		return false
	}
	s.leanDest = g.leanSources(dest, source, s.leanDest)
	// The game re-runs the same unleaned test once per dest lean cell.
	if len(s.leanDest) > 0 && g.lineOfSight(source, dest, true, 0, 0) {
		return true
	}
	s.leanSource = g.leanSources(source, dest, s.leanSource)
	for _, a := range s.leanSource {
		for _, b := range s.leanDest {
			if g.lineOfSight(a, b, true, 0, 0) {
				return true
			}
		}
	}
	return false
}
