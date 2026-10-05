package policy

// Suite blocks (#1215, #1951, epic #1938): a block is a wing off the main
// hallway whose rooms are sized per owner at siting. Each side packs its
// suites outward along the corridor, sharing walls. A block is planned at
// full size and never grows; a claim no planned suite answers sites another
// block, and sited suites never move or deepen.

// PlannedSuite is a suite's plan role.
const PlannedSuite PlannedRole = "suite"

// RoomRoleSuite is the room role a suite is built for. It is a plan role
// only: RimWorld has no Suite RoomRoleDef, so the native census reports a
// furnished suite as a Bedroom.
const RoomRoleSuite RoomRole = "Suite"

// Snapped suite bounds: width along the corridor, depth away from it.
const (
	suiteMinWidth, suiteMaxWidth int32 = 3, 7
	suiteMinDepth, suiteMaxDepth int32 = 4, 8
)

// suiteMaxRooms caps a suite block: six suites of 3-7 by 4-8 cover about
// the area of a bedroom wing's ten standard rooms.
const suiteMaxRooms = 6

// suiteFurnitureTiles is the bedroom template's footprint (bed 2, end
// table 1, dresser 2, lamp 1), each tile costing the Space stat 0.9.
const suiteFurnitureTiles = 6

func init() {
	RegisterInteriorTemplate(RoomRoleSuite, InteriorTemplate{Name: "suite", Plan: planBedroom})
}

// SuiteSize is the suite interior (width along the corridor, depth away
// from it) whose space meets an impressiveness target on its own: the
// Space stat normalised by 125 (room_quality_upgrade.go) reaching
// target/100, at 1.4 per open tile less 0.9 per furniture tile. The
// smallest area within 3-7 x 4-8 wins, then the squarer, then the
// narrower; a target no suite reaches gets the largest.
func SuiteSize(target float64) (int32, int32) {
	cells := suiteCells(target)
	bw, bd := suiteMaxWidth, suiteMaxDepth
	for w := suiteMinWidth; w <= suiteMaxWidth; w++ {
		for d := suiteMinDepth; d <= suiteMaxDepth; d++ {
			a, b := w*d, bw*bd
			if a < cells || a > b {
				continue
			}
			if a < b || abs32(w-d) < abs32(bw-bd) {
				bw, bd = w, d
			}
		}
	}
	return bw, bd
}

// corridorTo is the corridor's floor out to the wall row v.
func (f wingFrame) corridorTo(v int32) Rectangle {
	z, h := f.span(2, v-1)
	return f.orientRect(Rectangle{X: f.cx - SpineWidth/2, Z: z, Width: SpineWidth, Height: h})
}

// packSuites is one suite per target, sized by SuiteSize, packed outward
// along the corridor of frame f: each takes the side nearer the main
// hallway (east on a tie), sharing walls with its neighbour.
func packSuites(f wingFrame, targets []float64) []PlannedRoom {
	east, west := int32(3), int32(3)
	var out []PlannedRoom
	for _, t := range targets {
		w, d := SuiteSize(t)
		onEast := east <= west
		v0 := west
		if onEast {
			v0 = east
		}
		out = append(out, f.roomAt(onEast, v0, w, d, PlannedSuite))
		if onEast {
			east = v0 + w + 1
		} else {
			west = v0 + w + 1
		}
	}
	return out
}

// suiteWingGround is the ground a suite block keeps: the corridor and both
// sides out to the deepest suite it holds, walls included, no more.
func suiteWingGround(w Wing) Rectangle {
	f := frameOf(w)
	depth := int32(0)
	for _, r := range w.Rooms {
		in := f.orientRoom(r).Interior
		depth = max(depth, in.Width)
	}
	return f.ground(f.reachV(w.Rooms)-1, depth)
}

// suiteWings is the indexes of the suite blocks, in siting order.
func suiteWings(wings []Wing) []int {
	var out []int
	for i, w := range wings {
		if w.Purpose == WingSuites {
			out = append(out, i)
		}
	}
	return out
}

// carveSuiteWings takes every suite block's ground out of g.
func (g coreGrid) carveSuiteWings(wings []Wing) {
	for _, i := range suiteWings(wings) {
		g.carve(wingReserve(wings[i]))
	}
}
