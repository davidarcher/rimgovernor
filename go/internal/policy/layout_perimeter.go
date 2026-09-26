package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Perimeter, gates and killbox from terrain (#781, A5). The wall is a
// 3-thick rectangular ring around the core and the fields beside it; a
// stretch where rock, marsh or water already fills the whole thickness is
// left to the terrain. Raider approaches from the map edges are traced with
// the defense planner's flood (defense_arrivals.go), and the ring position
// most of them cross becomes the one opening: a killbox behind it with
// turret slots, and a bent approach lane outside. Gates of 3 doors through
// the wall's thickness stand along the rest of the ring. A cover-clear band
// runs 30 cells out, and the mortar spot is the open cell farthest from the
// wall.

// Reservation kinds A5 adds beside A1's.
const (
	ReserveTurret          ReservationKind = "turret"
	ReserveKillboxApproach ReservationKind = "killbox_approach"
)

const (
	perimeterThick int32 = 3
	// perimeterGap is the yard between the core and the wall; it holds
	// the killbox.
	perimeterGap int32 = 12
	// perimeterFieldReach is how far from the core a field chunk may
	// start and still sit inside the wall.
	perimeterFieldReach int32 = 15
	perimeterGatePitch  int32 = 20
	perimeterCoverBand  int32 = 30
	killboxHalf         int32 = 5
	killboxDepth        int32 = 10
	approachLeg         int32 = 8
)

// PlanPerimeter adds the wall, gates, killbox, turret slots, approach,
// cover-clear band and mortar spot to plan (which needs its core rooms),
// replacing any it held. A plan without rooms comes back unchanged.
func PlanPerimeter(plan LayoutPlan, s MapSurvey) LayoutPlan {
	w, h := s.Bounds.Width, s.Bounds.Height
	if len(plan.Rooms) == 0 || w < 1 || h < 1 {
		return plan
	}
	cells := map[domain.Cell]SurveyCell{}
	for _, c := range s.Cells {
		cells[c.Cell] = c
	}
	sealed := func(c domain.Cell) bool {
		sc, ok := cells[c]
		return ok && (sc.Rock || sc.Marsh || !sc.Walkable)
	}

	// The core box: rooms with walls and the hallway, then nearby fields.
	var core Rectangle
	grow := func(r Rectangle) {
		if core.Width == 0 {
			core = r
			return
		}
		x0, z0 := min(core.X, r.X), min(core.Z, r.Z)
		x1, z1 := max(core.X+core.Width, r.X+r.Width), max(core.Z+core.Height, r.Z+r.Height)
		core = Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
	}
	for _, r := range plan.Rooms {
		grow(pad(r.Interior, 1))
	}
	for _, sg := range plan.Spine {
		grow(pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	reach := pad(core, perimeterFieldReach)
	for _, z := range plan.Zones {
		if z.Kind != ZoneField {
			continue
		}
		near := false
		for _, r := range z.Runs {
			near = near || contains(reach, domain.Cell{X: r.X, Z: r.Z}) || contains(reach, domain.Cell{X: r.X + r.Length - 1, Z: r.Z})
		}
		if near {
			for _, r := range z.Runs {
				grow(Rectangle{X: r.X, Z: r.Z, Width: r.Length, Height: 1})
			}
		}
	}
	e := LayoutEdgeMargin
	outer := clipRect(pad(core, perimeterGap+perimeterThick), Rectangle{X: e, Z: e, Width: w - 2*e, Height: h - 2*e})
	inner := pad(outer, -perimeterThick)
	if inner.Width < 1 || inner.Height < 1 {
		return plan
	}

	sides := ringSides(outer)
	inRing := func(c domain.Cell) bool { return contains(outer, c) && !contains(inner, c) }
	sideOf := func(c domain.Cell) (int, int32) {
		for k, sd := range sides {
			if p := sd.pos(c); p >= sd.lo && p <= sd.hi {
				t := (c.X-sd.base(p).X)*sd.in.X + (c.Z-sd.base(p).Z)*sd.in.Z
				if t >= 0 && t < perimeterThick {
					return k, p
				}
			}
		}
		return -1, 0
	}

	// Approaches: every edge sector's shortest route to the core, through
	// the defense flood; the ring position most routes cross opens.
	site := defenseSite{r: DefenseRequest{Bounds: s.Bounds, Region: Rectangle{Width: w, Height: h}}, cells: map[domain.Cell]DefenseCell{}}
	for _, c := range s.Cells {
		pass := !sealed(c.Cell)
		row := DefenseCell{Cell: c.Cell, Passable: domain.Known(pass)}
		if pass && site.onBorder(c.Cell) {
			row.EdgeReachable = domain.Known(true)
		}
		site.cells[c.Cell] = row
	}
	mid := plan.Rooms[0].Door
	if len(plan.Spine) > 0 {
		sg := plan.Spine[0]
		mid = domain.Cell{X: (sg.From.X + sg.To.X) / 2, Z: (sg.From.Z + sg.To.Z) / 2}
	}
	origin, found := mid, false
	for x := inner.X; x < inner.X+inner.Width; x++ {
		for z := inner.Z; z < inner.Z+inner.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if site.passable(c) && (!found || squaredDistance(c, mid) < squaredDistance(origin, mid)) {
				origin, found = c, true
			}
		}
	}
	type crossing struct {
		side int
		pos  int32
	}
	votes, length := map[crossing]int{}, map[crossing]int{}
	if found {
		sectors, _ := site.sectors(origin)
		_, dist := site.paths(origin, nil)
		for _, sec := range sectors {
			best, ok := domain.Cell{}, false
			for _, c := range sec.Edge {
				if d, in := dist[c]; in && (!ok || d < dist[best]) {
					best, ok = c, true
				}
			}
			if !ok {
				continue
			}
			// The first ring cell on some shortest route, nearest the
			// edge: a raider meets the wall there.
			_, fromEdge := site.paths(best, nil)
			hit, hitOK := domain.Cell{}, false
			for c, de := range fromEdge {
				if do, in := dist[c]; !in || de+do != dist[best] || !inRing(c) {
					continue
				}
				if !hitOK || de < fromEdge[hit] || de == fromEdge[hit] && defenseCellLess(c, hit) {
					hit, hitOK = c, true
				}
			}
			if !hitOK {
				continue
			}
			if k, p := sideOf(hit); k >= 0 {
				x := crossing{k, p}
				votes[x]++
				length[x] += dist[best]
			}
		}
	}
	open, opened := crossing{}, false
	for x, v := range votes {
		o := votes[open]
		if !opened || v > o || v == o && (length[x] < length[open] || length[x] == length[open] && (x.side < open.side || x.side == open.side && x.pos < open.pos)) {
			open, opened = x, true
		}
	}

	var res []LayoutReservation
	add := func(kind ReservationKind, r Rectangle) {
		if r = clipRect(r, Rectangle{Width: w, Height: h}); r.Width > 0 && r.Height > 0 {
			res = append(res, LayoutReservation{Kind: kind, Area: r})
		}
	}
	var killbox Rectangle
	if opened {
		sd := sides[open.side]
		open.pos = max(sd.lo+1, min(sd.hi-1, open.pos))
		b := sd.base(open.pos)
		at := func(in, al int32) domain.Cell {
			return domain.Cell{X: b.X + sd.in.X*in + sd.al.X*al, Z: b.Z + sd.in.Z*in + sd.al.Z*al}
		}
		killbox = clipRect(rectOf(at(perimeterThick, -killboxHalf), at(perimeterThick+killboxDepth-1, killboxHalf)), inner)
		add(ReserveKillbox, killbox)
		for _, al := range []int32{-4, 0, 4} {
			add(ReserveTurret, rectOf(at(perimeterThick+killboxDepth-1, al), at(perimeterThick+killboxDepth-1, al)))
		}
		// The approach leaves the opening straight out, then turns along
		// the wall, so the lane breaks line of sight into the killbox.
		add(ReserveKillboxApproach, rectOf(at(-1, -1), at(-approachLeg, 1)))
		add(ReserveKillboxApproach, rectOf(at(-approachLeg+2, 2), at(-approachLeg, approachLeg+1)))
	}

	// Wall runs and gates, side by side.
	for k, sd := range sides {
		start := int32(-1)
		flush := func(end int32) {
			if start < 0 {
				return
			}
			add(ReservePerimeter, rectOf(sd.base(start), sd.cell(end, perimeterThick-1)))
			n := end - start + 1
			if n >= 5 {
				for off := min(perimeterGatePitch/2, n/2); off < n; off += perimeterGatePitch {
					p := start + off
					add(ReserveGate, rectOf(sd.base(p), sd.cell(p, perimeterThick-1)))
				}
			}
			start = -1
		}
		for p := sd.lo; p <= sd.hi; p++ {
			gap := opened && k == open.side && p >= open.pos-1 && p <= open.pos+1
			terrain := true
			for t := int32(0); t < perimeterThick; t++ {
				terrain = terrain && sealed(sd.cell(p, t))
			}
			if gap || terrain {
				flush(p - 1)
			} else if start < 0 {
				start = p
			}
		}
		flush(sd.hi)
	}

	// Cover-clear band: four strips around the ring.
	band := pad(outer, perimeterCoverBand)
	add(ReserveCoverClear, Rectangle{X: band.X, Z: band.Z, Width: band.Width, Height: outer.Z - band.Z})
	add(ReserveCoverClear, Rectangle{X: band.X, Z: outer.Z + outer.Height, Width: band.Width, Height: band.Z + band.Height - outer.Z - outer.Height})
	add(ReserveCoverClear, Rectangle{X: band.X, Z: outer.Z, Width: outer.X - band.X, Height: outer.Height})
	add(ReserveCoverClear, Rectangle{X: outer.X + outer.Width, Z: outer.Z, Width: band.X + band.Width - outer.X - outer.Width, Height: outer.Height})

	// Mortar: the open cell deepest inside the wall, off rooms, hallway
	// and killbox.
	taken := func(c domain.Cell) bool {
		if contains(killbox, c) {
			return true
		}
		for _, r := range plan.Rooms {
			if contains(pad(r.Interior, 1), c) {
				return true
			}
		}
		for _, sg := range plan.Spine {
			if contains(pad(rectOf(sg.From, sg.To), SpineWidth/2), c) {
				return true
			}
		}
		return false
	}
	mortar, depth := domain.Cell{}, int32(-1)
	for z := inner.Z; z < inner.Z+inner.Height; z++ {
		for x := inner.X; x < inner.X+inner.Width; x++ {
			c := domain.Cell{X: x, Z: z}
			sc, ok := cells[c]
			if !ok || !sc.Walkable || sc.Rock || sc.Marsh || taken(c) {
				continue
			}
			d := min(x-inner.X, z-inner.Z, inner.X+inner.Width-1-x, inner.Z+inner.Height-1-z)
			if d > depth {
				mortar, depth = c, d
			}
		}
	}
	if depth >= 0 {
		add(ReserveMortar, rectOf(mortar, mortar))
	}

	kept := plan.Reservations[:0:0]
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePerimeter, ReserveGate, ReserveKillbox, ReserveTurret, ReserveKillboxApproach, ReserveCoverClear, ReserveMortar:
		default:
			kept = append(kept, r)
		}
	}
	sort.SliceStable(res, func(i, j int) bool { return res[i].Kind < res[j].Kind })
	plan.Reservations = append(kept, res...)
	return plan
}

// ringSide is one side of the wall ring: positions lo..hi along al, the
// outer face at base(p), thickness toward in.
type ringSide struct {
	in, al   domain.Cell
	lo, hi   int32
	face     int32 // the outer face's row (south/north) or column (east/west)
	vertical bool
}

func (sd ringSide) base(p int32) domain.Cell {
	if sd.vertical {
		return domain.Cell{X: sd.face, Z: p}
	}
	return domain.Cell{X: p, Z: sd.face}
}

func (sd ringSide) cell(p, t int32) domain.Cell {
	b := sd.base(p)
	return domain.Cell{X: b.X + sd.in.X*t, Z: b.Z + sd.in.Z*t}
}

func (sd ringSide) pos(c domain.Cell) int32 {
	if sd.vertical {
		return c.Z
	}
	return c.X
}

// ringSides is south, north (full width, owning the corners), east, west.
func ringSides(o Rectangle) []ringSide {
	x1, z1 := o.X+o.Width-1, o.Z+o.Height-1
	return []ringSide{
		{in: domain.Cell{Z: 1}, al: domain.Cell{X: 1}, lo: o.X, hi: x1, face: o.Z},
		{in: domain.Cell{Z: -1}, al: domain.Cell{X: 1}, lo: o.X, hi: x1, face: z1},
		{in: domain.Cell{X: -1}, al: domain.Cell{Z: 1}, lo: o.Z + perimeterThick, hi: z1 - perimeterThick, face: x1, vertical: true},
		{in: domain.Cell{X: 1}, al: domain.Cell{Z: 1}, lo: o.Z + perimeterThick, hi: z1 - perimeterThick, face: o.X, vertical: true},
	}
}

func rectOf(a, b domain.Cell) Rectangle {
	x0, z0 := min(a.X, b.X), min(a.Z, b.Z)
	return Rectangle{X: x0, Z: z0, Width: max(a.X, b.X) - x0 + 1, Height: max(a.Z, b.Z) - z0 + 1}
}

func pad(r Rectangle, n int32) Rectangle {
	return Rectangle{X: r.X - n, Z: r.Z - n, Width: r.Width + 2*n, Height: r.Height + 2*n}
}

func clipRect(r, to Rectangle) Rectangle {
	x0, z0 := max(r.X, to.X), max(r.Z, to.Z)
	x1, z1 := min(r.X+r.Width, to.X+to.Width), min(r.Z+r.Height, to.Z+to.Height)
	if x1 <= x0 || z1 <= z0 {
		return Rectangle{}
	}
	return Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
}

func contains(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}
