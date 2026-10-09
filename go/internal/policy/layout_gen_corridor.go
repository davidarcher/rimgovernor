package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// layoutCounters tally growth events for the replay score gate, which
// logs them per fixture. They only count and never steer the plan.
var layoutCounters struct {
	routed, spineFallbacks, rings, secondDoors layoutCounter
}

// layoutCounter is a plain count: siting is serial (rule 2), so no atomics.
type layoutCounter int64

func (c *layoutCounter) Add(n int64)   { *c += layoutCounter(n) }
func (c *layoutCounter) Store(n int64) { *c = layoutCounter(n) }
func (c *layoutCounter) Load() int64   { return int64(*c) }

// Layout corridors. Once the rooms and
// housing blocks stand, the hallway network is finished in three passes,
// each kept only where the weighted walk (planWalk, the routeTrips edges
// each weighted by its affinity) shrinks:
//
//   - routeRings: straight-segment hallways between two free hallway ends,
//     routed around anything in the way (rooms, wings, obstacles), so the
//     network can close into a ring;
//   - addSecondDoors: a pass-through room (any role not in noThroughfare)
//     gets a second door where it touches a second hallway;
//   - hallEntrances: the entrances are the hallway ends that reach out of
//     the base, not the ends another hallway already covers.

// ringMax bounds how many rings a plan gets, and ringMaxLen how long (in
// cells) one ring hallway may be.
const (
	ringMax    = 2
	ringMaxLen = 90
)

// hallBands is every cell a hallway of segs covers.
func hallBands(segs []SpineSegment) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range spineRects(segs) {
		for _, c := range rectCells(r) {
			out[c] = true
		}
	}
	return out
}

// hallEnd is one end of a hallway: its centre cell and the step leaving it.
type hallEnd struct {
	cell, out domain.Cell
}

// freeEnds are the spine's hallway ends no other hallway covers: where the
// network meets the edge of the base. A hallway end inside another
// hallway's band is a junction, not an end.
func freeEnds(spine []SpineSegment) []hallEnd {
	bands := spineRects(spine)
	var out []hallEnd
	for i, s := range spine {
		lo, hi := s.From, s.To
		var ends [2]hallEnd
		if alongX(s) {
			if lo.X > hi.X {
				lo, hi = hi, lo
			}
			ends = [2]hallEnd{{lo, domain.Cell{X: -1}}, {hi, domain.Cell{X: 1}}}
		} else {
			if lo.Z > hi.Z {
				lo, hi = hi, lo
			}
			ends = [2]hallEnd{{lo, domain.Cell{Z: -1}}, {hi, domain.Cell{Z: 1}}}
		}
		for _, e := range ends {
			covered := false
			for j, b := range bands {
				covered = covered || j != i && contains(b, e.cell)
			}
			if !covered {
				out = append(out, e)
			}
		}
	}
	return out
}

// hallEntrances are the cells traffic enters the base from: the end slab
// of every free hallway end (a wing corridor is a dead end, never one).
func hallEntrances(spine []SpineSegment) []domain.Cell {
	var out []domain.Cell
	seen := map[domain.Cell]bool{}
	for _, e := range freeEnds(spine) {
		for d := -SpineWidth / 2; d <= SpineWidth/2; d++ {
			c := domain.Cell{X: e.cell.X, Z: e.cell.Z + d}
			if e.out.X == 0 {
				c = domain.Cell{X: e.cell.X + d, Z: e.cell.Z}
			}
			if !seen[c] {
				seen[c] = true
				out = append(out, c)
			}
		}
	}
	return out
}

// planBlocked is every cell a new hallway must keep off: every room's
// walls and interior and every wing's reserved ground.
func planBlocked(p LayoutPlan) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		for _, c := range rectCells(roomWalls(r)) {
			out[c] = true
		}
	}
	for _, w := range p.Wings {
		for _, c := range rectCells(wingReserve(w)) {
			out[c] = true
		}
	}
	return out
}

// routeRings closes the hallway network into rings: for each pair of free
// hallway ends it routes the shortest straight-segment hallway between
// them over the ground g leaves (centre cells whose whole 3x3 is core
// ground clear of rooms, wings and the other hallways), and keeps the pair
// whose ring shortens the weighted walk most, up to ringMax rings. A ring
// that shortens nothing is never added.
func (g coreGrid) routeRings(p LayoutPlan) LayoutPlan {
	for round := 0; round < ringMax; round++ {
		base := planWalk(p)
		blocked := planBlocked(p)
		bands := hallBands(p.Hallways())
		ends := freeEnds(p.Spine)
		var best []SpineSegment
		bestGain, bestLen := 0, 0
		for i := range ends {
			for j := i + 1; j < len(ends); j++ {
				path := g.ringPath(ends[i], ends[j], blocked, bands)
				if path == nil {
					continue
				}
				trial := p
				trial.Spine = append(append([]SpineSegment(nil), p.Spine...), path...)
				trial.Entrances = hallEntrances(trial.Spine)
				if _, err := CheckRoutes(trial); err != nil {
					continue
				}
				gain := base - planWalk(trial)
				length := 0
				for _, s := range path {
					length += int(abs32(s.To.X-s.From.X) + abs32(s.To.Z-s.From.Z))
				}
				if gain > bestGain || gain == bestGain && gain > 0 && length < bestLen {
					best, bestGain, bestLen = trial.Spine, gain, length
				}
			}
		}
		if bestGain <= 0 {
			break
		}
		layoutCounters.rings.Add(1)
		p.Spine = best
		p.Entrances = hallEntrances(best)
	}
	return p
}

// ringPath is the shortest straight-run hallway from end a to end b (as
// segments, the first starting on a's centre cell and the last ending on
// b's), or nil when none fits within ringMaxLen cells. A centre cell is
// open when its 3x3 is core ground clear of blocked and of the existing
// hallway bands, except the two ends' own slabs.
func (g coreGrid) ringPath(a, b hallEnd, blocked, bands map[domain.Cell]bool) []SpineSegment {
	if abs32(a.cell.X-b.cell.X)+abs32(a.cell.Z-b.cell.Z) > ringMaxLen {
		return nil
	}
	allow := map[domain.Cell]bool{}
	for _, e := range []hallEnd{a, b} {
		for dx := int32(-1); dx <= 1; dx++ {
			for dz := int32(-1); dz <= 1; dz++ {
				allow[domain.Cell{X: e.cell.X + dx, Z: e.cell.Z + dz}] = true
			}
		}
	}
	open := func(c domain.Cell) bool {
		for dx := int32(-1); dx <= 1; dx++ {
			for dz := int32(-1); dz <= 1; dz++ {
				n := domain.Cell{X: c.X + dx, Z: c.Z + dz}
				if !g.core[n] || blocked[n] || bands[n] && !allow[n] {
					return false
				}
			}
		}
		return true
	}
	start := domain.Cell{X: a.cell.X + a.out.X, Z: a.cell.Z + a.out.Z}
	goal := domain.Cell{X: b.cell.X + b.out.X, Z: b.cell.Z + b.out.Z}
	if !open(start) || !open(goal) {
		return nil
	}
	prev := map[domain.Cell]domain.Cell{start: start}
	depth := map[domain.Cell]int{start: 0}
	queue := []domain.Cell{start}
	for head := 0; head < len(queue); head++ {
		if _, ok := prev[goal]; ok {
			break
		}
		c := queue[head]
		if depth[c] >= ringMaxLen {
			continue
		}
		// Straight steps first, so equal paths keep their runs long.
		for _, d := range [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			n := domain.Cell{X: c.X + d[0], Z: c.Z + d[1]}
			if _, seen := prev[n]; seen || !open(n) {
				continue
			}
			prev[n], depth[n] = c, depth[c]+1
			queue = append(queue, n)
		}
	}
	if _, ok := prev[goal]; !ok {
		return nil
	}
	cells := []domain.Cell{b.cell, goal}
	for c := goal; c != start; {
		c = prev[c]
		cells = append(cells, c)
	}
	cells = append(cells, a.cell)
	// cells runs b to a; the runs are the same either way.
	var out []SpineSegment
	runStart := cells[0]
	var dir [2]int32
	for i := 1; i < len(cells); i++ {
		d := [2]int32{cells[i].X - cells[i-1].X, cells[i].Z - cells[i-1].Z}
		if i > 1 && d != dir {
			out = append(out, SpineSegment{From: runStart, To: cells[i-1]})
			runStart = cells[i-1]
		}
		dir = d
	}
	return append(out, SpineSegment{From: runStart, To: cells[len(cells)-1]})
}

// addSecondDoors gives every pass-through room a second door where it
// touches a hallway other than the one its first door opens on, when that
// shortens the weighted walk. Rooms that are noThroughfare never get one: a
// second door makes a thoroughfare of them, and a fixed room keeps
// the doors it has.
func (p LayoutPlan) addSecondDoors(fixed map[Rectangle]bool) LayoutPlan {
	halls := p.Hallways()
	bands := spineRects(halls)
	hallAt := func(c domain.Cell) int {
		for i, b := range bands {
			if contains(b, c) {
				return i
			}
		}
		return -1
	}
	rooms := append([]PlannedRoom(nil), p.Rooms...)
	p.Rooms = rooms
	for i := range rooms {
		r := rooms[i]
		if noThroughfare[r.Role] || fixed[r.Interior] {
			continue
		}
		primary := -1
		if side, ok := doorSide(r.Interior, r.Door); ok {
			primary = hallAt(outside(r.Door, side))
		}
		for _, cand := range secondDoorCandidates(r, hallAt, primary) {
			trial := append([]PlannedRoom(nil), rooms...)
			trial[i].Doors = append(append([]Door(nil), rooms[i].Doors...), cand)
			q := p
			q.Rooms = trial
			if planWalk(q) < planWalk(p) {
				if _, err := CheckRoutes(q); err == nil {
					rooms, p.Rooms = trial, trial
					layoutCounters.secondDoors.Add(1)
				}
			}
		}
	}
	return p
}

// outside is the cell beyond door d, away from the room whose wall side it
// faces.
func outside(d domain.Cell, side domain.Rotation) domain.Cell {
	switch side {
	case domain.South:
		d.Z--
	case domain.North:
		d.Z++
	case domain.West:
		d.X--
	case domain.East:
		d.X++
	}
	return d
}

// secondDoorCandidates are the doors r could add: for each wall side and
// each hallway (other than primary) its wall touches, the middle wall cell
// of the stretch beside that hallway. A corner, and a cell holding a door
// or the link already, is never a door.
func secondDoorCandidates(r PlannedRoom, hallAt func(domain.Cell) int, primary int) []Door {
	in := r.Interior
	taken := map[domain.Cell]bool{r.Door: true}
	for _, d := range r.Doors {
		taken[d.Cell] = true
	}
	if r.Link != nil {
		taken[*r.Link] = true
	}
	type side struct {
		rot   domain.Rotation
		cells []domain.Cell
	}
	var sides []side
	var south, north, west, east []domain.Cell
	for x := in.X; x < in.X+in.Width; x++ {
		south = append(south, domain.Cell{X: x, Z: in.Z - 1})
		north = append(north, domain.Cell{X: x, Z: in.Z + in.Height})
	}
	for z := in.Z; z < in.Z+in.Height; z++ {
		west = append(west, domain.Cell{X: in.X - 1, Z: z})
		east = append(east, domain.Cell{X: in.X + in.Width, Z: z})
	}
	sides = []side{{domain.South, south}, {domain.North, north}, {domain.West, west}, {domain.East, east}}
	var out []Door
	for _, s := range sides {
		// Runs of wall cells beside one hallway.
		for i := 0; i < len(s.cells); {
			h := hallAt(outside(s.cells[i], s.rot))
			j := i
			for j < len(s.cells) && hallAt(outside(s.cells[j], s.rot)) == h {
				j++
			}
			if h >= 0 && h != primary {
				run := s.cells[i:j]
				mid := run[len(run)/2]
				if !taken[mid] {
					out = append(out, Door{Cell: mid, Rot: s.rot})
				}
			}
			i = j
		}
	}
	return out
}
