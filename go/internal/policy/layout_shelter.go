package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shelter (#2041, epic #2037): one temporary starter room holding a
// sleeping spot per colonist, campfires on a cold map, a crafting spot and a
// research table. It is sited apart from the other rooms so its ground frees cleanly
// when it is demolished, and sized from those contents. Its interior is the
// shelter template's (interior_shelter.go); only the counts are here.

const (
	// shelterGap is the clear ground, in cells, the shelter prefers between
	// its walls and every other room's walls.
	shelterGap int32 = 5
	// shelterReach bounds the search for open ground round the seed.
	shelterReach int32 = 40
	// shelterMaxDepth is the deepest shelter interior.
	shelterMaxDepth int32 = 6
	// shelterMinSide is the shortest shelter interior side.
	shelterMinSide int32 = 3
	// shelterMinDepth is the shallowest shelter interior: the research table
	// (3x2) and the open row in front of it that it is worked from.
	shelterMinDepth int32 = 4
)

// Contents, in cells. A sleeping spot is 1x2, the simple research bench 3x2,
// the crafting spot and a campfire one cell each.
const (
	shelterBunkCells    = 2
	shelterCampfireCell = 1
	shelterCoolerCell   = 1
	shelterCraftCells   = 1
	shelterResearchCell = 6
)

// ColdMapBelowC is the seasonal minimum below which a map is cold (#2044): a
// pinned constant, not the room latches' ColdEnter.
const ColdMapBelowC = 0.0

// coldMapCampfires is how many campfires a cold map's shelter holds indoors.
const coldMapCampfires = 2

// ColdMapCurve reports whether a seasonal outdoor temperature curve (the
// twelfths' means, C) dips below ColdMapBelowC. An empty curve is not cold.
func ColdMapCurve(curve []float64) bool {
	for _, t := range curve {
		if t < ColdMapBelowC {
			return true
		}
	}
	return false
}

// ShelterCampfires is how many campfires the shelter holds indoors (#2044):
// two on a cold map, none elsewhere, where the cooking campfire stands outside.
func ShelterCampfires(cold bool) int {
	if cold {
		return coldMapCampfires
	}
	return 0
}

// HotMapCurve reports whether a seasonal outdoor temperature curve peaks above
// the temperature planner's HotEnter (#2044). An empty curve is not hot.
func HotMapCurve(curve []float64) bool {
	hot := DefaultRoundsPolicy().HotEnter
	for _, t := range curve {
		if t > hot {
			return true
		}
	}
	return false
}

// ShelterCoolers is how many passive coolers the shelter template holds on its
// floor (#2044): one on a hot map, none elsewhere. A powered Cooler is
// wall-mounted and needs no floor, but a tribal-tier shelter has none.
func ShelterCoolers(hot bool) int {
	if hot {
		return 1
	}
	return 0
}

// ShelterInteriorArea is the interior cells a shelter for colonists needs:
// its contents plus half again for the aisle and the door's approach. The
// research bench is counted at its 3x2 footprint (#2042).
func ShelterInteriorArea(colonists, campfires, coolers int) int {
	contents := shelterBunkCells*max(colonists, 1) + shelterCampfireCell*max(campfires, 0) + shelterCoolerCell*max(coolers, 0) + shelterCraftCells + shelterResearchCell
	return (contents*3 + 1) / 2
}

// ShelterSizes are the shelter interiors (width, depth) holding at least
// ShelterInteriorArea cells, best first: the fewest cells, then the aspect
// nearest two thirds.
func ShelterSizes(colonists, campfires, coolers int) [][2]int32 {
	area := ShelterInteriorArea(colonists, campfires, coolers)
	var out [][2]int32
	for d := shelterMinDepth; d <= shelterMaxDepth; d++ {
		w := max(int32((area+int(d)-1)/int(d)), shelterMinSide)
		if w >= d {
			out = append(out, [2]int32{w, d})
		}
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if ca, cb := a[0]*a[1], b[0]*b[1]; ca != cb {
			return ca < cb
		}
		return abs32(2*a[0]-3*a[1]) < abs32(2*b[0]-3*b[1])
	})
	return out
}

// siteShelter adds the shelter to a base plan's rooms unless it holds one.
// The preferred ground is shelterGap cells clear of every other room; where
// that fits nowhere the gap shrinks a cell at a time, and last the shelter
// takes an ordinary slot on a hallway (packRoom), so the first roof is never
// delayed by a site that cannot be had. g is the core ground the rest of the
// plan left; seed is the base's centre, the shelter's door faces it.
func (g coreGrid) siteShelter(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, seed domain.Cell, colonists int, cold, hot bool) ([]SpineSegment, []LayoutRoom) {
	if g.noShelter {
		return spine, rooms
	}
	for _, r := range rooms {
		if r.Role == ModuleShelter {
			return spine, rooms
		}
	}
	sizes := ShelterSizes(colonists, ShelterCampfires(cold), ShelterCoolers(hot))
	all := append([]LayoutRoom(nil), rooms...)
	for _, w := range wings {
		all = append(all, w.Rooms...)
	}
	halls := spineRects(LayoutPlan{Spine: spine, Wings: wings}.Hallways())
	for gap := shelterGap; gap >= 1; gap-- {
		for _, size := range sizes {
			if room, ok := g.shelterOnOpenGround(all, halls, seed, size, gap); ok {
				return spine, append(rooms, room)
			}
		}
	}
	for _, size := range sizes {
		if next, grown, placed, _ := g.packRoom(spine, rooms, wings, ModuleShelter, size); placed {
			return next, grown
		}
	}
	return spine, rooms
}

// shelterOnOpenGround is the best site whose walls sit on core ground, clear
// of the hallways and gap cells from every room: the fewest rich-soil cells,
// then the nearest to seed. Its door is at the middle of the wall facing seed.
func (g coreGrid) shelterOnOpenGround(rooms []LayoutRoom, halls []Rectangle, seed domain.Cell, size [2]int32, gap int32) (LayoutRoom, bool) {
	w, d := size[0], size[1]
	var avoid []Rectangle
	for _, r := range rooms {
		avoid = append(avoid, pad(roomWalls(r), gap))
	}
	for _, h := range halls {
		avoid = append(avoid, pad(h, 1))
	}
	best, bestRich, bestDist, found := LayoutRoom{}, 0, int32(0), false
	for x := seed.X - shelterReach; x <= seed.X+shelterReach; x++ {
		for z := seed.Z - shelterReach; z <= seed.Z+shelterReach; z++ {
			in := Rectangle{X: x, Z: z, Width: w, Height: d}
			if g.skip[in] || shelterBlocked(pad(in, 1), avoid) {
				continue
			}
			rich, ok := g.shelterGround(pad(in, 1))
			if !ok {
				continue
			}
			dist := distanceToRect(seed, in)
			if !found || rich < bestRich || rich == bestRich && dist < bestDist {
				best, bestRich, bestDist, found = shelterRoom(in, seed), rich, dist, true
			}
		}
	}
	return best, found
}

func shelterBlocked(walls Rectangle, avoid []Rectangle) bool {
	for _, a := range avoid {
		if rectsOverlap(walls, a) {
			return true
		}
	}
	return false
}

// shelterGround reports the rich-soil cells under walls and whether every
// cell is core ground.
func (g coreGrid) shelterGround(walls Rectangle) (int, bool) {
	rich := 0
	for x := walls.X; x < walls.X+walls.Width; x++ {
		for z := walls.Z; z < walls.Z+walls.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if !g.core[c] {
				return 0, false
			}
			if g.soil[c] == soilCostRich {
				rich++
			}
		}
	}
	return rich, true
}

// distanceToRect is the Chebyshev distance from c to r.
func distanceToRect(c domain.Cell, r Rectangle) int32 {
	dx := max(r.X-c.X, 0, c.X-(r.X+r.Width-1))
	dz := max(r.Z-c.Z, 0, c.Z-(r.Z+r.Height-1))
	return max(dx, dz)
}

// shelterRoom is the room on interior in with its door at the middle of the
// wall facing seed.
func shelterRoom(in Rectangle, seed domain.Cell) LayoutRoom {
	cx, cz := in.X+in.Width/2, in.Z+in.Height/2
	dx, dz := seed.X-cx, seed.Z-cz
	r := LayoutRoom{Role: ModuleShelter, Interior: in}
	switch {
	case abs32(dx) > abs32(dz) && dx > 0:
		r.Door, r.DoorRot = domain.Cell{X: in.X + in.Width, Z: cz}, domain.East
	case abs32(dx) > abs32(dz):
		r.Door, r.DoorRot = domain.Cell{X: in.X - 1, Z: cz}, domain.West
	case dz > 0:
		r.Door, r.DoorRot = domain.Cell{X: cx, Z: in.Z + in.Height}, domain.North
	default:
		r.Door, r.DoorRot = domain.Cell{X: cx, Z: in.Z - 1}, domain.South
	}
	return r
}
