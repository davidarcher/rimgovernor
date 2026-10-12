package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shelter: one temporary starter room holding a
// sleeping spot per colonist, campfires on a cold map, a crafting spot and a
// research table. It is sited apart from the other rooms so its ground frees cleanly
// when it is demolished, and sized from those contents. Its interior is the
// shelter template's (interior_shelter.go); only the counts are here.

const (
	// shelterMaxDepth is the deepest shelter interior.
	shelterMaxDepth int32 = 6
	// shelterMinSide is the shortest shelter interior side.
	shelterMinSide int32 = 3
	// shelterMinDepth is the shallowest shelter interior: the research table
	// (3x2) and the open row in front of it that it is worked from.
	shelterMinDepth int32 = 4
)

// Contents, in cells. A sleeping spot is 1x2, the simple research bench 3x2,
// the crafting spot and a campfire one cell each (the hosted capabilities'
// pieces are capabilityCells).
const (
	shelterBunkCells    = 2
	shelterCampfireCell = 1
	shelterCoolerCell   = 1
)

// ColdMapBelowC is the seasonal minimum below which a map is cold: a
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

// ShelterCampfires is how many campfires the shelter holds indoors:
// two on a cold map, none elsewhere, where the cooking campfire stands outside.
func ShelterCampfires(cold bool) int {
	if cold {
		return coldMapCampfires
	}
	return 0
}

// HotMapCurve reports whether a seasonal outdoor temperature curve peaks above
// the temperature planner's HotEnter. An empty curve is not hot.
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
// floor: one on a hot map, none elsewhere. A powered Cooler is
// wall-mounted and needs no floor, but a tribal-tier shelter has none.
func ShelterCoolers(hot bool) int {
	if hot {
		return 1
	}
	return 0
}

// ShelterInteriorArea is the interior cells a shelter for colonists needs:
// its contents plus half again for the aisle and the door's approach. The
// research bench is counted at its 3x2 footprint.
func ShelterInteriorArea(colonists, campfires, coolers int) int {
	contents := shelterBunkCells*max(colonists, 1) + shelterCampfireCell*max(campfires, 0) + shelterCoolerCell*max(coolers, 0)
	for _, c := range shelterCapabilities {
		contents += capabilityCells[c]
	}
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

// shelterRoom is the room on interior in with its door at the middle of the
// wall facing seed.
func shelterRoom(in Rectangle, seed domain.Cell) PlannedRoom {
	cx, cz := in.X+in.Width/2, in.Z+in.Height/2
	dx, dz := seed.X-cx, seed.Z-cz
	r := PlannedRoom{Role: PlannedShelter, Region: RectRegion(in)}
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

// shelterWithChildren is the shelter room followed by its children: an airlock
// outside its door when airlock is set and a battery porch on the far wall
// when porch is set (a cold map). The children carry no Parent yet; WithRoomIDs
// hands it out with their ids.
func shelterWithChildren(in Rectangle, seed domain.Cell, airlock, porch bool) []PlannedRoom {
	r := shelterRoom(in, seed)
	out := []PlannedRoom{r}
	if airlock {
		out = append(out, attachAirlock(r.Door, r.DoorRot))
	}
	if porch {
		out = append(out, attachPorch(in, r.DoorRot))
	}
	return out
}
