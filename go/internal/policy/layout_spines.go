package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// wingAnchor is the storage room's door, which the bedroom wing hugs: the
// starter shell stands on the storage slot, so the first bedrooms are a
// short walk from camp (#1178).
func wingAnchor(rooms []LayoutRoom) (domain.Cell, bool) {
	for _, r := range rooms {
		if r.Role == ModuleStorage {
			return r.Door, true
		}
	}
	return domain.Cell{}, false
}

// segmentOrder is the order hallways are tried for role. Bedrooms form one
// wing: the hallway already holding bedrooms first, else the storage
// room's, then the rest nearest the storage door first (#1178). Other roles
// keep spine order.
func segmentOrder(spine []SpineSegment, rooms []LayoutRoom, role ModuleRole) []int {
	order := make([]int, len(spine))
	for i := range order {
		order[i] = i
	}
	anchor, ok := wingAnchor(rooms)
	if role != ModuleBedroom || !ok {
		return order
	}
	rank := func(i int) int64 {
		for _, r := range rooms {
			if r.Role == ModuleBedroom && onSegment(r, spine[i]) {
				return -2
			}
		}
		for _, r := range rooms {
			if r.Role == ModuleStorage && onSegment(r, spine[i]) {
				return -1
			}
		}
		s := spine[i]
		dx, dz := int64((s.From.X+s.To.X)/2-anchor.X), int64((s.From.Z+s.To.Z)/2-anchor.Z)
		return dx*dx + dz*dz
	}
	sort.SliceStable(order, func(a, b int) bool { return rank(order[a]) < rank(order[b]) })
	return order
}

// Spines and crossings (#952). Spine[0] is the main east-west hallway; every
// later segment is a north-south crossing laid through it, so the core grows
// from a line into a + and then an H (a crossing at each end of the main
// hallway). A crossing always runs out on both sides of the main hallway,
// never one, so no branch turns into an L or U. The line beyond each
// hallway's ends stays clear of the other hallways' rooms, so it can still
// grow straight.

// spineMaxLen caps a hallway's length before rooms move on to the next one.
const spineMaxLen int32 = 64

// maxCrossings are the centre crossing and one at each end of the main hallway.
const maxCrossings = 3

func alongX(s SpineSegment) bool { return s.From.Z == s.To.Z }

// onSegment reports r's door opening onto s's hallway.
func onSegment(r LayoutRoom, s SpineSegment) bool {
	lo, hi := s.From, s.To
	if alongX(s) {
		d := r.Door.Z - s.From.Z
		return (d == 2 || d == -2) && r.Door.X >= min(lo.X, hi.X) && r.Door.X <= max(lo.X, hi.X)
	}
	d := r.Door.X - s.From.X
	return (d == 2 || d == -2) && r.Door.Z >= min(lo.Z, hi.Z) && r.Door.Z <= max(lo.Z, hi.Z)
}

// segmentGrid is the core as spine i sees it: the other hallways, with
// their lines extended past each end by the core's half cross-section, and
// the other hallways' rooms (walls included) are taken; spine i's own
// hallway line stays open. A crossing is returned transposed (X and Z
// swapped), so the east-west placement code serves it too.
func (g coreGrid) segmentGrid(spine []SpineSegment, i int, rooms []LayoutRoom) (coreGrid, SpineSegment, []LayoutRoom) {
	own := spine[i]
	ownLine := func(c domain.Cell) bool {
		if alongX(own) {
			return c.Z >= own.From.Z-SpineWidth/2 && c.Z <= own.From.Z+SpineWidth/2
		}
		return c.X >= own.From.X-SpineWidth/2 && c.X <= own.From.X+SpineWidth/2
	}
	local := coreGrid{core: make(map[domain.Cell]bool, len(g.core)), rock: g.rock, maxLen: spineMaxLen, near: g.near, hasNear: g.hasNear}
	for c := range g.core {
		local.core[c] = true
	}
	take := func(r Rectangle, keepLine bool) {
		for x := r.X; x < r.X+r.Width; x++ {
			for z := r.Z; z < r.Z+r.Height; z++ {
				c := domain.Cell{X: x, Z: z}
				if !keepLine || !ownLine(c) {
					delete(local.core, c)
				}
			}
		}
	}
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
		take(band, true)
	}
	var mine []LayoutRoom
	for _, r := range rooms {
		if onSegment(r, own) {
			mine = append(mine, r)
		} else {
			take(roomWalls(r), false)
		}
	}
	// The main hallway balances about its centre crossing, a crossing about
	// the main hallway.
	if i > 0 {
		local.junction, local.hasJunction = spine[0].From.Z, true
	} else if len(spine) > 1 {
		local.junction, local.hasJunction = spine[1].From.X, true
	}
	if alongX(own) {
		return local, own, mine
	}
	t := coreGrid{core: transposeSet(local.core), rock: transposeSet(local.rock), maxLen: local.maxLen, junction: local.junction, hasJunction: true, near: transposeCell(local.near), hasNear: local.hasNear}
	for k := range mine {
		mine[k] = transposeRoom(mine[k])
	}
	return t, SpineSegment{From: transposeCell(own.From), To: transposeCell(own.To)}, mine
}

// placeOn puts role's room on spine i, returning the grown segment.
func (g coreGrid) placeOn(spine []SpineSegment, i int, rooms []LayoutRoom, role ModuleRole, size [2]int32) (SpineSegment, LayoutRoom, bool) {
	local, seg, mine := g.segmentGrid(spine, i, rooms)
	room, ok := local.placeSized(&seg, mine, role, size)
	if !ok {
		return spine[i], LayoutRoom{}, false
	}
	if alongX(spine[i]) {
		return seg, room, true
	}
	return SpineSegment{From: transposeCell(seg.From), To: transposeCell(seg.To)}, transposeRoom(room), true
}

// crossing lays a north-south crossing through the main hallway at the
// column nearest x (searching out to reach cells either way) whose full
// cross-section is core ground clear of every room. The main hallway is
// stretched to meet it.
func (g coreGrid) crossing(spine []SpineSegment, rooms []LayoutRoom, x, reach int32) (SpineSegment, SpineSegment, bool) {
	main := spine[0]
	z0 := main.From.Z
	half := SpineWidth/2 + coreMaxDepth + 2
	clear := func(cx int32) bool {
		for _, s := range spine[1:] {
			if cx-s.From.X < 2*SpineWidth && s.From.X-cx < 2*SpineWidth {
				return false
			}
		}
		band := Rectangle{X: cx - SpineWidth/2, Z: z0 - half, Width: SpineWidth, Height: 2*half + 1}
		for x := band.X; x < band.X+band.Width; x++ {
			for z := band.Z; z < band.Z+band.Height; z++ {
				if !g.core[domain.Cell{X: x, Z: z}] {
					return false
				}
			}
		}
		for _, r := range rooms {
			if rectsOverlap(band, roomWalls(r)) {
				return false
			}
		}
		// The main hallway must reach the crossing over core ground.
		for x := min(cx, main.From.X); x <= max(cx, main.To.X); x++ {
			for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
				if !g.core[domain.Cell{X: x, Z: z0 + dz}] {
					return false
				}
			}
		}
		return true
	}
	for d := int32(0); d <= reach; d++ {
		for _, cx := range []int32{x + d, x - d} {
			if !clear(cx) {
				continue
			}
			main.From.X, main.To.X = min(main.From.X, cx), max(main.To.X, cx)
			return main, SpineSegment{From: domain.Cell{X: cx, Z: z0 - SpineWidth/2}, To: domain.Cell{X: cx, Z: z0 + SpineWidth/2}}, true
		}
	}
	return spine[0], SpineSegment{}, false
}

// addCrossing adds the next crossing: the centre one, then one past each
// end of the main hallway.
func (g coreGrid) addCrossing(spine []SpineSegment, rooms []LayoutRoom) ([]SpineSegment, bool) {
	if len(spine) == 0 || len(spine) > maxCrossings || !alongX(spine[0]) {
		return spine, false
	}
	main := spine[0]
	var tries [][2]int32 // column, reach
	if len(spine) == 1 {
		tries = append(tries, [2]int32{(main.From.X + main.To.X) / 2, (main.To.X-main.From.X)/2 + 1})
	}
	tries = append(tries, [2]int32{main.To.X + SpineWidth/2 + 1, 0}, [2]int32{main.From.X - SpineWidth/2 - 1, 0})
	for _, t := range tries {
		m, c, ok := g.crossing(spine, rooms, t[0], t[1])
		if !ok {
			continue
		}
		out := append([]SpineSegment{m}, spine[1:]...)
		return append(out, c), true
	}
	return spine, false
}

func rectsOverlap(a, b Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}

func transposeCell(c domain.Cell) domain.Cell { return domain.Cell{X: c.Z, Z: c.X} }

func transposeSet(s map[domain.Cell]bool) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(s))
	for c := range s {
		out[transposeCell(c)] = true
	}
	return out
}

// transposeRoom swaps X and Z; a door facing its hallway to the south faces
// it to the west once transposed, north to east, and back.
func transposeRoom(r LayoutRoom) LayoutRoom {
	in := r.Interior
	r.Interior = Rectangle{X: in.Z, Z: in.X, Width: in.Height, Height: in.Width}
	r.Door = transposeCell(r.Door)
	if r.Link != nil {
		l := transposeCell(*r.Link)
		r.Link = &l
	}
	switch r.DoorRot {
	case domain.North:
		r.DoorRot = domain.East
	case domain.East:
		r.DoorRot = domain.North
	case domain.South:
		r.DoorRot = domain.West
	case domain.West:
		r.DoorRot = domain.South
	}
	return r
}
