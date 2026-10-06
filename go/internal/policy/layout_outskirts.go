package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The outskirts (#2183, epic #2176): one off-core cluster per colony holding
// the morgue, tomb, graveyard, waste yard and incinerator. It is a reservation
// sited apart from the core spine: set back outskirtsGap cells from every
// room (the dump clearance and the corpse-sight radius), off the straight
// growth lines beyond each hallway's ends, outside what the core ring takes in
// and with a walkable path to the core. The side is derived on every plan from
// the core ground each side holds, never stored: the colony grows toward core
// ground, so the cluster goes where least of it lies. It reuses the shelter's
// gap test (shelterBlocked) and the pens' grid (utilityGrid.free, inset and
// outside). The rooms inside it belong to the children of #2176.

// ReserveOutskirts is the outskirts cluster's outline: the region its rooms
// are sited in, reserved so the packer and later sites leave it whole.
const ReserveOutskirts ReservationKind = "outskirts"

const (
	// outskirtsGap is the clear ground, in cells, between the cluster and
	// every room's walls.
	outskirtsGap int32 = 6
	// outskirtsReach is how far beyond the core's extent a side's ground is
	// counted.
	outskirtsReach int32 = shelterReach
)

// outskirtsSides are the sides of the core extent in tie-break order.
var outskirtsSides = []domain.Rotation{domain.West, domain.East, domain.South, domain.North}

// OutskirtsArea is the plan's outskirts reservation, false when it holds none.
func (p LayoutPlan) OutskirtsArea() (Rectangle, bool) {
	for _, r := range p.Reservations {
		if r.Kind == ReserveOutskirts {
			return r.Area, true
		}
	}
	return Rectangle{}, false
}

// growOutskirts reserves a w x h outline for the outskirts in plan unless it
// holds one: an existing cluster never moves. It reports whether it added one.
// A zero size asks for none.
func growOutskirts(plan LayoutPlan, size [2]int32) (LayoutPlan, bool) {
	if size[0] <= 0 || size[1] <= 0 {
		return plan, false
	}
	if _, has := plan.OutskirtsArea(); has {
		return plan, false
	}
	area, ok := SiteOutskirts(plan, size[0], size[1])
	if !ok {
		return plan, false
	}
	plan.Reservations = append(slices.Clone(plan.Reservations), LayoutReservation{Kind: ReserveOutskirts, Area: area})
	return plan, true
}

// SiteOutskirts is the w x h outline for the outskirts on plan's core ground:
// on the side of the core extent holding the least core ground within reach,
// the nearest valid site to the extent, ties by position. plan holds its core
// zones. False when the plan has no core or no side fits one.
func SiteOutskirts(plan LayoutPlan, w, h int32) (Rectangle, bool) {
	best, u, ext, lines := outskirtsCandidates(plan, w, h)
	var out Rectangle
	found, score := false, 0
	for _, side := range outskirtsSides {
		area, ok := best[side]
		if !ok {
			continue
		}
		if n := u.sideGround(ext, side, lines); !found || n < score {
			out, found, score = area, true, n
		}
	}
	return out, found
}

// outskirtsCandidates is the nearest valid site on each side of the core
// extent, with the grid, extent and growth lines that scored them.
func outskirtsCandidates(plan LayoutPlan, w, h int32) (map[domain.Rotation]Rectangle, *utilityGrid, Rectangle, []Rectangle) {
	ext, ok := plan.CoreBounds()
	if !ok || len(plan.Hallways()) == 0 {
		return nil, nil, ext, nil
	}
	u := newUtilityGrid(plan)
	if u.w == 0 || u.h == 0 {
		return nil, nil, ext, nil
	}
	lines := growthLines(plan, u)
	var avoid []Rectangle
	for _, r := range plan.AllRooms() {
		avoid = append(avoid, pad(roomWalls(r), outskirtsGap))
	}
	for _, s := range spineRects(plan.Hallways()) {
		avoid = append(avoid, pad(s, outskirtsGap))
	}
	walkable := u.walkableFromCore(plan)
	fp := coreBaseFootprint(plan, u.w, u.h)
	gap := yardGap(fp, u.w, u.h, plan.YardCells)

	best, bestDist := map[domain.Rotation]Rectangle{}, map[domain.Rotation]int32{}
	x0, x1 := max(ext.X-outskirtsReach-w, 0), min(ext.X+ext.Width+outskirtsReach, u.w-w)
	z0, z1 := max(ext.Z-outskirtsReach-h, 0), min(ext.Z+ext.Height+outskirtsReach, u.h-h)
	for z := z0; z <= z1; z++ {
		for x := x0; x <= x1; x++ {
			site := Rectangle{X: x, Z: z, Width: w, Height: h}
			side, dist, beyond := sideOf(ext, site)
			if !beyond {
				continue
			}
			if _, has := best[side]; has && dist >= bestDist[side] {
				continue
			}
			if !u.free(site, false) || !u.inset(site) || !u.outside(site) || crowdsCore(fp, u.w, u.h, site, gap) ||
				shelterBlocked(site, avoid) || shelterBlocked(site, lines) || !u.reachable(site, walkable) {
				continue
			}
			best[side], bestDist[side] = site, dist
		}
	}
	return best, u, ext, lines
}

// sideOf is the side of ext that site lies wholly beyond, the Chebyshev gap to
// ext, and whether it lies beyond any side. A corner site takes the side its
// larger offset lies on.
func sideOf(ext, site Rectangle) (domain.Rotation, int32, bool) {
	west := ext.X - (site.X + site.Width)
	east := site.X - (ext.X + ext.Width)
	south := ext.Z - (site.Z + site.Height)
	north := site.Z - (ext.Z + ext.Height)
	side, dist := domain.West, west
	for _, c := range []struct {
		s domain.Rotation
		d int32
	}{{domain.East, east}, {domain.South, south}, {domain.North, north}} {
		if c.d > dist {
			side, dist = c.s, c.d
		}
	}
	return side, dist, dist >= 0
}

// growthLines are the strips the core may still grow along: each hallway
// stretched to the map edge along its own axis, widened by the core's half
// cross-section (the band growSpine and the packer keep clear).
func growthLines(plan LayoutPlan, u *utilityGrid) []Rectangle {
	var out []Rectangle
	for _, s := range plan.Hallways() {
		r := spineRects([]SpineSegment{s})[0]
		if alongX(s) {
			out = append(out, Rectangle{X: 0, Z: r.Z - coreHalf, Width: u.w, Height: r.Height + 2*coreHalf})
		} else {
			out = append(out, Rectangle{X: r.X - coreHalf, Z: 0, Width: r.Width + 2*coreHalf, Height: u.h})
		}
	}
	return out
}

// sideGround counts the core ground on side of ext within reach, off the
// growth lines and free: the ground the core could still take that way.
func (u *utilityGrid) sideGround(ext Rectangle, side domain.Rotation, lines []Rectangle) int {
	slab := Rectangle{X: ext.X - outskirtsReach, Z: ext.Z - outskirtsReach, Width: ext.Width + 2*outskirtsReach, Height: ext.Height + 2*outskirtsReach}
	switch side {
	case domain.West:
		slab.Width = outskirtsReach
	case domain.East:
		slab.X, slab.Width = ext.X+ext.Width, outskirtsReach
	case domain.South:
		slab.Z, slab.Height = ext.Z-outskirtsReach, outskirtsReach
	default:
		slab.Z, slab.Height = ext.Z+ext.Height, outskirtsReach
	}
	n := 0
	for z := max(slab.Z, 0); z < min(slab.Z+slab.Height, u.h); z++ {
		for x := max(slab.X, 0); x < min(slab.X+slab.Width, u.w); x++ {
			if !u.ok[z*u.w+x] || u.used[z*u.w+x] {
				continue
			}
			if shelterBlocked(Rectangle{X: x, Z: z, Width: 1, Height: 1}, lines) {
				continue
			}
			n++
		}
	}
	return n
}

// walkableFromCore marks the cells a pawn can walk to from the core's
// hallways over open ground: not rock, not no-go, not under a room, hallway or
// reservation (a hallway is where the walk ends).
func (u *utilityGrid) walkableFromCore(plan LayoutPlan) []bool {
	reach := make([]bool, u.w*u.h)
	open := func(x, z int32) bool {
		i := z*u.w + x
		return u.in(x, z) && !u.rock[i] && !u.used[i] && (u.ok[i] || u.field[i])
	}
	var queue []domain.Cell
	for _, s := range spineRects(plan.Hallways()) {
		for z := s.Z - 1; z <= s.Z+s.Height; z++ {
			for x := s.X - 1; x <= s.X+s.Width; x++ {
				if u.in(x, z) && open(x, z) && !reach[z*u.w+x] {
					reach[z*u.w+x] = true
					queue = append(queue, domain.Cell{X: x, Z: z})
				}
			}
		}
	}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, d := range [][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			x, z := c.X+d[0], c.Z+d[1]
			if u.in(x, z) && open(x, z) && !reach[z*u.w+x] {
				reach[z*u.w+x] = true
				queue = append(queue, domain.Cell{X: x, Z: z})
			}
		}
	}
	return reach
}

// reachable reports a cell next to site's outline on the walkable set.
func (u *utilityGrid) reachable(site Rectangle, walkable []bool) bool {
	ring := pad(site, 1)
	for z := ring.Z; z < ring.Z+ring.Height; z++ {
		for x := ring.X; x < ring.X+ring.Width; x++ {
			if u.in(x, z) && walkable[z*u.w+x] && !contains(site, domain.Cell{X: x, Z: z}) {
				return true
			}
		}
	}
	return false
}
