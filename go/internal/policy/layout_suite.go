package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The suite wing (#1215, epic #1200): a second wing off the main hallway,
// sited only once a suite is wanted, whose rooms are sized per owner. Each
// side packs its suites outward along the corridor, sharing walls; a suite
// grows away from the corridor (#1218), so the wing keeps the ground out
// to the largest depth free on both sides and past its open end.

// ModuleSuite is a suite's plan role.
const ModuleSuite ModuleRole = "suite"

// RoomRoleSuite is the room role a suite is built for. It is a plan role
// only: RimWorld has no Suite RoomRoleDef, so the native census reports a
// furnished suite as a Bedroom.
const RoomRoleSuite RoomRole = "Suite"

// Snapped suite bounds: width along the corridor, depth away from it.
const (
	suiteMinWidth, suiteMaxWidth int32 = 3, 7
	suiteMinDepth, suiteMaxDepth int32 = 4, 8
)

// suiteFurnitureTiles is the bedroom template's footprint (bed 2, end
// table 1, dresser 2, lamp 1), each tile costing the Space stat 0.9.
const suiteFurnitureTiles = 6

// suiteGrowthReserve is how many suites past the wanted count the wing
// keeps ground for at its open end.
const suiteGrowthReserve = 2

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

// suiteCursor is where each side's next suite starts along the corridor.
func suiteCursor(f wingFrame, rooms []LayoutRoom) (east, west int32) {
	east, west = 3, 3
	for _, r := range rooms {
		v0, w := f.along(r)
		if r.Interior.X > f.cx {
			east = max(east, v0+w+1)
		} else {
			west = max(west, v0+w+1)
		}
	}
	return east, west
}

// nextSuite is the next suite of interior w x d on the side whose cursor
// is nearer the main hallway (east on a tie), and its ground grown to the
// largest depth, walls included.
func nextSuite(f wingFrame, rooms []LayoutRoom, w, d int32) (LayoutRoom, Rectangle) {
	east, west := suiteCursor(f, rooms)
	v0, onEast := east, true
	if west < east {
		v0, onEast = west, false
	}
	grown := roomWalls(f.roomAt(onEast, v0, w, suiteMaxDepth, ModuleSuite))
	return f.roomAt(onEast, v0, w, d, ModuleSuite), grown
}

// corridorTo is the corridor's floor out to the wall row v.
func (f wingFrame) corridorTo(v int32) Rectangle {
	z, h := f.span(2, v-1)
	return Rectangle{X: f.cx - SpineWidth/2, Z: z, Width: SpineWidth, Height: h}
}

// suiteReserve is the ground the suite wing keeps: both sides at the
// largest depth, out past its open end for want suites (at least those it
// has) plus suiteGrowthReserve more at the largest width.
func suiteReserve(w Wing, want int) Rectangle {
	f := frameOf(w)
	east, west := suiteCursor(f, w.Rooms)
	extra := int32((max(want-len(w.Rooms), 0) + suiteGrowthReserve + 1) / 2)
	return f.ground(max(east, west)-2+extra*(suiteMaxWidth+1), suiteMaxDepth)
}

// growSuites sites the suite wing off the main hallway once targets asks
// for a suite, and adds suites at its open end until it holds one per
// target, sized by SuiteSize; existing suites never move or shrink. Suites
// that do not fit (grown to the largest depth) are left out.
func (g coreGrid) growSuites(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, targets []float64) ([]SpineSegment, []Wing) {
	if len(spine) == 0 || !alongX(spine[0]) {
		return spine, wings
	}
	idx := wingOf(wings, WingSuites)
	have := 0
	if idx >= 0 {
		have = len(wings[idx].Rooms)
	}
	if have >= len(targets) {
		return spine, wings
	}
	free := g.wingGround(spine, rooms)
	fits := func(f wingFrame, placed []LayoutRoom, target float64) (LayoutRoom, bool) {
		w, d := SuiteSize(target)
		r, grown := nextSuite(f, placed, w, d)
		v0, _ := f.along(r)
		return r, free(grown) && free(f.corridorTo(v0+w))
	}
	wings = append([]Wing(nil), wings...)
	var f wingFrame
	if idx < 0 {
		var ok bool
		f, ok = g.siteWing(spine, rooms, func(try wingFrame) int {
			var placed []LayoutRoom
			for _, t := range targets {
				r, ok := fits(try, placed, t)
				if !ok {
					break
				}
				placed = append(placed, r)
			}
			return len(placed)
		})
		if !ok {
			return spine, wings
		}
		spine = openWing(spine, f)
		base := domain.Cell{X: f.cx, Z: f.z(2)}
		wings = append(wings, Wing{Purpose: WingSuites, Corridor: SpineSegment{From: base, To: base}})
		idx = len(wings) - 1
	} else {
		f = frameOf(wings[idx])
	}
	w := wings[idx]
	for _, t := range targets[len(w.Rooms):] {
		r, ok := fits(f, w.Rooms, t)
		if !ok {
			break
		}
		r.Dug = g.dug(r)
		trial := w
		trial.Rooms = append(append([]LayoutRoom(nil), w.Rooms...), r)
		trial.Corridor.To = f.reach(trial.Rooms)
		next := append([]Wing(nil), wings...)
		next[idx] = trial
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Rooms: rooms, Wings: next}); err != nil {
			break
		}
		w = trial
	}
	if len(w.Rooms) == 0 {
		return spine, append(wings[:idx], wings[idx+1:]...)
	}
	wings[idx] = w
	return spine, wings
}

// grownSuite is suite r (of wing w) deepened away from the corridor to the
// smallest depth, up to suiteMaxDepth, whose floor meets target's space
// (#1218); the door and the width along the corridor stay. False when r
// already meets target, is at its largest depth, or the strip it grows
// into (walls included) is not free core ground in plan.
func grownSuite(plan LayoutPlan, w Wing, r LayoutRoom, target float64) (LayoutRoom, bool) {
	f := frameOf(w)
	in := r.Interior
	width, depth := in.Height, in.Width
	cells := suiteCells(target)
	if width*depth >= cells || depth >= suiteMaxDepth {
		return r, false
	}
	d := depth
	for d < suiteMaxDepth && width*d < cells {
		d++
	}
	grown := r
	strip := Rectangle{Z: in.Z - 1, Width: d - depth, Height: width + 2}
	if in.X > f.cx {
		grown.Interior.Width = d
		strip.X = in.X + depth + 1
	} else {
		grown.Interior.X, grown.Interior.Width = in.X-(d-depth), d
		strip.X = grown.Interior.X - 1
	}
	g := newCoreGrid(plan.Zones, plan.Reservations)
	g.carveBedroomWings(plan.Wings, 0)
	if !g.wingGround(plan.Spine, plan.Rooms)(strip) {
		return r, false
	}
	grown.Dug = r.Dug || g.dug(grown)
	return grown, true
}

// suiteWingRoom finds r among the plan's suites: its wing and index.
func suiteWingRoom(plan LayoutPlan, r LayoutRoom) (Wing, bool) {
	if i := wingOf(plan.Wings, WingSuites); i >= 0 {
		for _, s := range plan.Wings[i].Rooms {
			if s.Interior == r.Interior {
				return plan.Wings[i], true
			}
		}
	}
	return Wing{}, false
}

// SuitesOwed reports whether Grow would change the suite wing for
// targets: a suite to add, or an existing one to grow outward.
func SuitesOwed(plan LayoutPlan, targets []float64) bool {
	if len(targets) > plan.SuiteRooms() {
		return true
	}
	if i := wingOf(plan.Wings, WingSuites); i >= 0 {
		w := plan.Wings[i]
		for k, r := range w.Rooms {
			if k < len(targets) && targets[k] > 0 {
				if _, ok := grownSuite(plan, w, r, targets[k]); ok {
					return true
				}
			}
		}
	}
	return false
}

// growSuitesOutward grows each existing suite whose entry in targets asks
// for more floor than it has (#1218); a zero entry keeps the suite. The
// new plan row carries the grown interior; the ladder then raises the new
// ring, takes the old wall down and re-sites the furniture
// (NextSuiteGrowth).
func growSuitesOutward(plan LayoutPlan, wings []Wing, targets []float64) []Wing {
	idx := wingOf(wings, WingSuites)
	if idx < 0 {
		return wings
	}
	w := wings[idx]
	var rooms []LayoutRoom
	for i, r := range w.Rooms {
		if i >= len(targets) || targets[i] <= 0 {
			continue
		}
		if grown, ok := grownSuite(plan, w, r, targets[i]); ok {
			if rooms == nil {
				rooms = append([]LayoutRoom(nil), w.Rooms...)
			}
			rooms[i] = grown
		}
	}
	if rooms == nil {
		return wings
	}
	wings = append([]Wing(nil), wings...)
	w.Rooms = rooms
	wings[idx] = w
	return wings
}
