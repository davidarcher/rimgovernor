package policy

// Suite blocks of the old generator: a block is a wing off the main
// hallway whose rooms are sized per owner at siting. Each side packs its
// suites outward along the corridor, sharing walls. A block is planned at
// full size and never grows. Pending deletion with the generator (#2969).

// suiteMaxRooms caps a suite block: six suites of 3-7 by 4-8 cover about
// the area of a bedroom wing's ten standard rooms.
const suiteMaxRooms = 6

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
		in := f.orientRoom(r).Region.Bounds()
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
