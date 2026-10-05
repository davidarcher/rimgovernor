package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The slot packer (#2097, epic #2092). packRoom puts one room on the nearest
// free slot of the core: a hallway is worked in its own frame (u along it, a
// across it), so a north-south crossing is packed by the same code as the
// main hallway and no grid is cloned or transposed. A room's door sits in its
// hallway wall; neighbours on a side share their side walls; a relation rule
// (besideRoles) hangs a room off its neighbour's wall first.

// hallFrame is one spine segment in its own frame. lo..hi is its extent along
// it and line the coordinate it runs at (Z when along X, X otherwise).
type hallFrame struct {
	along  bool
	line   int32
	lo, hi int32
}

func newHallFrame(s SpineSegment) hallFrame {
	if alongX(s) {
		return hallFrame{along: true, line: s.From.Z, lo: min(s.From.X, s.To.X), hi: max(s.From.X, s.To.X)}
	}
	return hallFrame{line: s.From.X, lo: min(s.From.Z, s.To.Z), hi: max(s.From.Z, s.To.Z)}
}

// segment is the hallway as a spine segment.
func (h hallFrame) segment() SpineSegment {
	return SpineSegment{From: h.cell(h.lo, h.line), To: h.cell(h.hi, h.line)}
}

// cell is the cell at u along the hallway and a across it.
func (h hallFrame) cell(u, a int32) domain.Cell {
	if h.along {
		return domain.Cell{X: u, Z: a}
	}
	return domain.Cell{X: a, Z: u}
}

// rect is the w (along) by d (across) rectangle whose near corner is (u, a).
func (h hallFrame) rect(u, a, w, d int32) Rectangle {
	if h.along {
		return Rectangle{X: u, Z: a, Width: w, Height: d}
	}
	return Rectangle{X: a, Z: u, Width: d, Height: w}
}

// extent is r's span along the hallway, [u, u+w), and across it, [a, a+d).
func (h hallFrame) extent(r Rectangle) (u, w, a, d int32) {
	if h.along {
		return r.X, r.Width, r.Z, r.Height
	}
	return r.Z, r.Height, r.X, r.Width
}

// positive reports r on the north (or east) side of the hallway.
func (h hallFrame) positive(r Rectangle) bool {
	_, _, a, _ := h.extent(r)
	return a > h.line
}

// facing is the way a door on the hallway's positive or negative side faces.
func (h hallFrame) facing(pos bool) domain.Rotation {
	switch {
	case h.along && pos:
		return domain.South
	case h.along:
		return domain.North
	case pos:
		return domain.West
	}
	return domain.East
}

// room is a w by d room at u on one side of the hallway, its door in the
// hallway wall at the middle of its width.
func (h hallFrame) room(role ModuleRole, u, w, d int32, pos bool) LayoutRoom {
	a, wall := h.line-2-d, h.line-2
	if pos {
		a, wall = h.line+3, h.line+2
	}
	return LayoutRoom{Role: role, Interior: h.rect(u, a, w, d), Door: h.cell(u+w/2, wall), DoorRot: h.facing(pos)}
}

// packer is the core as one hallway sees it: the other hallways' bands (their
// lines extended past each end by the core's half cross-section) and the
// walls of the rooms that open on other hallways are taken, the hallway's own
// line stays open. mine are the rooms on this hallway.
type packer struct {
	g      coreGrid
	h      hallFrame
	mine   []LayoutRoom
	barred []Rectangle
	bands  []Rectangle
	// junction is where another hallway crosses this one (u): rooms go to
	// whichever end stays nearer it, so the hallway grows about its crossing.
	junction    int32
	hasJunction bool
}

func (g coreGrid) packer(spine []SpineSegment, i int, rooms []LayoutRoom) packer {
	own := spine[i]
	p := packer{g: g, h: newHallFrame(own)}
	half := SpineWidth/2 + coreMaxDepth + 2
	for j, s := range spine {
		if j == i {
			continue
		}
		band := spineRects([]SpineSegment{s})[0]
		if alongX(s) {
			band.X, band.Width = band.X-half, band.Width+2*half
		} else {
			band.Z, band.Height = band.Z-half, band.Height+2*half
		}
		p.bands = append(p.bands, band)
	}
	for _, r := range rooms {
		if onSegment(r, own) {
			p.mine = append(p.mine, r)
		} else {
			p.barred = append(p.barred, roomWalls(r))
		}
	}
	switch {
	case i > 0:
		if main := spine[0]; alongX(main) != p.h.along {
			p.junction, p.hasJunction = newHallFrame(main).line, true
		}
	case len(spine) > 1:
		p.junction, p.hasJunction = newHallFrame(spine[1]).line, true
	}
	return p
}

// open reports every cell of r core ground this hallway may take.
func (p packer) open(r Rectangle) bool {
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			if !p.g.core[domain.Cell{X: x, Z: z}] {
				return false
			}
		}
	}
	for _, b := range p.barred {
		if rectsOverlap(r, b) {
			return false
		}
	}
	for _, b := range p.bands {
		if !rectsOverlap(r, b) {
			continue
		}
		// A band takes everything but the cells on this hallway's own line.
		x0, z0 := max(r.X, b.X), max(r.Z, b.Z)
		x1, z1 := min(r.X+r.Width, b.X+b.Width), min(r.Z+r.Height, b.Z+b.Height)
		lo, hi := z0, z1-1
		if !p.h.along {
			lo, hi = x0, x1-1
		}
		if lo < p.h.line-SpineWidth/2 || hi > p.h.line+SpineWidth/2 {
			return false
		}
	}
	return true
}

// fits reports the room with its walls, and the hallway beside it, open
// ground, and its interior not a slot SiteRoom has scored.
func (p packer) fits(room LayoutRoom) bool {
	if p.g.skip[room.Interior] || !p.open(roomWalls(room)) {
		return false
	}
	// The hallway between the room and the hallway's current extent must
	// be continuous.
	u, w, _, _ := p.h.extent(room.Interior)
	a, b := u-1, u+w
	if b < p.h.lo {
		b = p.h.lo
	} else if a > p.h.hi {
		a = p.h.hi
	}
	return p.open(p.h.rect(a, p.h.line-SpineWidth/2, b-a+1, SpineWidth))
}

// extend stretches the hallway to cover a..b along it.
func (h *hallFrame) extend(a, b int32) {
	h.lo, h.hi = min(h.lo, a), max(h.hi, b)
}

// finish dresses a placed room: rock to dig, and the hallway grown over it.
func (p *packer) finish(room LayoutRoom) LayoutRoom {
	u, w, _, _ := p.h.extent(room.Interior)
	p.h.extend(u-1, u+w)
	room.Dug = p.g.dug(room)
	return room
}

// slot is the nearest free slot for a w by d room on the hallway: at
// whichever end of the rooms keeps the hallway shortest (or nearest its
// junction), the hallway growing out from its centre.
func (p *packer) slot(role ModuleRole, w, d int32) (LayoutRoom, bool) {
	pick, picked, pickGrow := LayoutRoom{}, false, int32(0)
	for _, east := range []bool{true, false} {
		var best LayoutRoom
		bestU, found := int32(0), false
		for _, pos := range []bool{true, false} {
			// Walls on this side: the next slot's near wall is the last
			// room's far wall (shared).
			edge, any := p.h.lo-1, false
			for _, r := range p.mine {
				if p.h.positive(r.Interior) != pos {
					continue
				}
				u, rw, _, _ := p.h.extent(r.Interior)
				if east && (!any || u+rw > edge) {
					edge = u + rw
				}
				if !east && (!any || u-1 < edge) {
					edge = u - 1
				}
				any = true
			}
			for shift := int32(0); shift < 64; shift++ {
				u := edge - w - shift
				if east {
					u = edge + 1 + shift
				}
				room := p.h.room(role, u, w, d, pos)
				if !p.fits(room) {
					continue
				}
				if !found || east && u < bestU || !east && u > bestU {
					best, bestU, found = room, u, true
				}
				break
			}
		}
		if !found {
			continue
		}
		lo, hi := min(p.h.lo, bestU-1), max(p.h.hi, bestU+w)
		if hi-lo > spineMaxLen {
			continue
		}
		grow := hi - lo - (p.h.hi - p.h.lo)
		if p.hasJunction {
			grow = max(hi-p.junction, p.junction-lo)
		}
		if !picked || grow < pickGrow {
			pick, picked, pickGrow = best, true, grow
		}
	}
	if !picked {
		return LayoutRoom{}, false
	}
	return p.finish(pick), true
}

// against places role by its relation rule to its neighbour's walls: the
// neighbour's east side, then west, a Link door in the shared wall unless the
// rule is unlinked; its back wall before or after the sides as the rule says.
// It reports false for a role with no rule, without the neighbour or when no
// wall fits (the caller then takes a plain slot).
func (p *packer) against(rooms []LayoutRoom, role ModuleRole) (LayoutRoom, bool) {
	rule, ok := besideRoles[role]
	if !ok {
		return LayoutRoom{}, false
	}
	var k Rectangle
	found := false
	for _, r := range rooms {
		if r.Role == rule.neighbour {
			k, found = r.Interior, true
			break
		}
	}
	if !found {
		return LayoutRoom{}, false
	}
	if rule.backFirst {
		if room, ok := p.behind(rooms, k, role, rule); ok {
			return room, true
		}
	}
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	ku, kw, ka, kd := p.h.extent(k)
	pos := p.h.positive(k)
	for _, east := range []bool{true, false} {
		u, wall := ku+kw+1, ku+kw
		if !east {
			u, wall = ku-1-w, ku-1
		}
		room := p.h.room(role, u, w, d, pos)
		if !p.fits(room) || overlapsRooms(room, rooms) {
			continue
		}
		_, _, ra, rd := p.h.extent(room.Interior)
		lo, hi := max(ra, ka), min(ra+rd, ka+kd)
		if lo >= hi {
			continue
		}
		if !rule.unlinked {
			link := p.h.cell(wall, (lo+hi-1)/2)
			room.Link = &link
		}
		return p.finish(room), true
	}
	if rule.backLast {
		return p.behind(rooms, k, role, rule)
	}
	return LayoutRoom{}, false
}

// behind places role against its neighbour's back wall, the one opposite the
// hallway, entered only through a Link door in that wall. The butchery sits
// behind the freezer: the butcher walks through the freezer, and carcasses
// stay in the cold. A gear room (#1773) takes the back wall when the side
// walls are taken. The room's own Door is the Link.
func (p *packer) behind(rooms []LayoutRoom, k Rectangle, role ModuleRole, rule relationRule) (LayoutRoom, bool) {
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	ku, kw, ka, kd := p.h.extent(k)
	pos := p.h.positive(k)
	a, wall := ka-1-d, ka-1
	if pos {
		a, wall = ka+kd+1, ka+kd
	}
	// The freezer's cooler takes the middle of that wall and vents straight
	// out behind it, so the butchery overlaps only its far or near end cell;
	// a gear room lines up with an edge of its neighbour.
	columns := []int32{ku, ku + kw - w}
	if rule.endCell {
		columns = []int32{ku + kw - 1, ku - w + 1}
	}
	for _, u := range columns {
		room := LayoutRoom{Role: role, Interior: p.h.rect(u, a, w, d), DoorRot: p.h.facing(pos)}
		if !p.fits(room) || overlapsRooms(room, rooms) {
			continue
		}
		lo, hi := max(u, ku), min(u+w, ku+kw)
		if lo >= hi {
			continue
		}
		link := p.h.cell((lo+hi-1)/2, wall)
		room.Door, room.Link = link, &link
		room.Dug = p.g.dug(room)
		return room, true
	}
	return LayoutRoom{}, false
}

// packRoom places one room of role on the nearest free slot, trying every
// hallway and laying a crossing while no slot fits. fit reports that some
// slot fit the role (a room that makes a thoroughfare, #780, still counts as
// fit); placed that a room was added.
func (g coreGrid) packRoom(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, role ModuleRole, size [2]int32) (_ []SpineSegment, _ []LayoutRoom, placed, fit bool) {
	for !placed {
		for i := range spine {
			p := g.packer(spine, i, rooms)
			ok := false
			var room LayoutRoom
			if i == 0 {
				room, ok = p.against(rooms, role)
			}
			if !ok {
				room, ok = p.slot(role, size[0], size[1])
			}
			if !ok {
				continue
			}
			fit = true
			// A room that makes a thoroughfare (#780) is left out; the
			// next hallway (or role) tries its slot.
			trial := append(append([]LayoutRoom(nil), rooms...), room)
			grown := append([]SpineSegment(nil), spine...)
			grown[i] = p.h.segment()
			if _, err := CheckRoutes(LayoutPlan{Spine: grown, Entrances: spineEntrances(grown), Rooms: trial, Wings: wings}); err != nil {
				continue
			}
			spine, rooms, placed = grown, trial, true
			break
		}
		if placed || fit {
			break
		}
		next, ok := g.growSpine(spine, rooms)
		if !ok {
			break
		}
		spine = next
	}
	return spine, rooms, placed, fit
}
