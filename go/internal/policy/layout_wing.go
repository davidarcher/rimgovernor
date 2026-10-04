package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Bedroom wings (#1213, epic #1200). The standard bedrooms form one wing: a
// SpineWidth corridor branching off the main hallway, with identical rooms
// on both sides. Neighbouring rooms on a side share their walls and every
// door sits in the corridor wall, so no room is a thoroughfare. A wing is
// planned at full size when sited and never grows; a pawn past its capacity
// sites another wing, and sited rooms never move.

// WingPurpose is what a wing's rooms are for.
type WingPurpose string

// WingBedrooms is the standard bedroom wing; WingSuites the suite wing
// (#1215, layout_suite.go).
const (
	WingBedrooms WingPurpose = "bedrooms"
	WingSuites   WingPurpose = "suites"
	// WingBedroomsRetiring is a standard wing whose rooms are smaller than
	// the tier's WingRoomSize (#1219): it keeps its ground,
	// and its pawns migrate to an active wing (NextMigrateStep).
	WingBedroomsRetiring WingPurpose = "bedrooms_retiring"
)

// retireWings marks every bedroom wing whose rooms are smaller than tier's
// WingRoomSize Retiring (#1219); the next wing sited takes the new size.
func retireWings(wings []Wing, tier BuildTier) []Wing {
	want := WingRoomSize(tier)
	var out []Wing
	for _, w := range wings {
		if w.Purpose == WingBedrooms && len(w.Rooms) > 0 {
			if s := frameOf(w).size; s[0]*s[1] < want[0]*want[1] {
				w.Purpose = WingBedroomsRetiring
			}
		}
		out = append(out, w)
	}
	return out
}

// Wing is a corridor off the main hallway with its rooms. Corridor.From is
// its centre cell in the main hallway's wall row, Corridor.To the open end.
type Wing struct {
	Purpose  WingPurpose
	Corridor SpineSegment
	Rooms    []LayoutRoom
}

// WingRoomSize is a standard room's interior for tier (#1214, epic #1200):
// width along the corridor, depth away from it. Camp and Masonry rooms are
// 3x4, Powered and Industrial 4x4, Spacer 4x5. A new wing takes the
// current tier's size; an existing wing keeps the size of its rooms.
func WingRoomSize(tier BuildTier) [2]int32 {
	switch {
	case tier >= BuildTierSpacer:
		return [2]int32{4, 5}
	case tier >= BuildTierPowered:
		return [2]int32{4, 4}
	}
	return [2]int32{3, 4}
}

// wingMaxRooms is a bedroom wing's size (#1950, epic #1938): a wing is
// planned at this many rooms when sited and never grows; once every wing
// is occupied, a new one is sited.
const wingMaxRooms = 10

// AllRooms is every planned room: the spine's, then each wing's.
func (p LayoutPlan) AllRooms() []LayoutRoom {
	if len(p.Wings) == 0 {
		return p.Rooms
	}
	out := append([]LayoutRoom(nil), p.Rooms...)
	for _, w := range p.Wings {
		out = append(out, w.Rooms...)
	}
	return out
}

// Hallways is every walkable hallway: the spine, then each wing's corridor.
func (p LayoutPlan) Hallways() []SpineSegment {
	if len(p.Wings) == 0 {
		return p.Spine
	}
	out := append([]SpineSegment(nil), p.Spine...)
	for _, w := range p.Wings {
		out = append(out, w.Corridor)
	}
	return out
}

// wingFrame places a wing: v counts cells away from the main hallway's
// centre row z0 (sign +1 north, -1 south) along the corridor at column cx.
// size is a standard wing's room interior, as WingRoomSize.
type wingFrame struct {
	cx, z0, sign int32
	size         [2]int32
}

// frameOf is w's frame; a standard wing's rooms keep the size of its first.
func frameOf(w Wing) wingFrame {
	sign := int32(1)
	if w.Corridor.To.Z < w.Corridor.From.Z {
		sign = -1
	}
	size := WingRoomSize(BuildTierCamp)
	if (w.Purpose == WingBedrooms || w.Purpose == WingBedroomsRetiring) && len(w.Rooms) > 0 {
		in := w.Rooms[0].Interior
		size = [2]int32{in.Height, in.Width}
	}
	return wingFrame{cx: w.Corridor.From.X, z0: w.Corridor.From.Z - 2*sign, sign: sign, size: size}
}

func (f wingFrame) z(v int32) int32 { return f.z0 + f.sign*v }

// span is the Z range of v0..v0+n-1.
func (f wingFrame) span(v0, n int32) (int32, int32) {
	if f.sign > 0 {
		return f.z0 + v0, n
	}
	return f.z0 - v0 - n + 1, n
}

// room is the wing's k-th room: even k east of the corridor, odd west,
// k/2 slots out from the main hallway.
func (f wingFrame) room(k int) LayoutRoom {
	w := f.size[0]
	return f.roomAt(k%2 == 0, 3+int32(k/2)*(w+1), w, f.size[1], ModuleBedroom)
}

// roomAt is a role room of interior w along the corridor by d away from
// it, east or west of the corridor over v0..v0+w-1, its door mid-side in
// the corridor wall.
func (f wingFrame) roomAt(east bool, v0, w, d int32, role ModuleRole) LayoutRoom {
	z, h := f.span(v0, w)
	door := domain.Cell{Z: f.z(v0 + w/2)}
	r := LayoutRoom{Role: role}
	if east {
		r.Interior = Rectangle{X: f.cx + 3, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx+2, domain.West
	} else {
		r.Interior = Rectangle{X: f.cx - 2 - d, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx-2, domain.East
	}
	r.Door = door
	return r
}

// along is r's first cell out along the corridor (v0) and its width there.
func (f wingFrame) along(r LayoutRoom) (int32, int32) {
	if f.sign > 0 {
		return r.Interior.Z - f.z0, r.Interior.Height
	}
	return f.z0 - (r.Interior.Z + r.Interior.Height - 1), r.Interior.Height
}

// reach is the corridor's open end serving rooms: the wall row past the
// farthest one.
func (f wingFrame) reach(rooms []LayoutRoom) domain.Cell {
	v := int32(2)
	for _, r := range rooms {
		v0, w := f.along(r)
		v = max(v, v0+w)
	}
	return domain.Cell{X: f.cx, Z: f.z(v)}
}

// ground is the wing's ground out to v=2+n-1 with rooms depth d each
// side, walls included.
func (f wingFrame) ground(n, d int32) Rectangle {
	z, h := f.span(2, n)
	return Rectangle{X: f.cx - 3 - d, Z: z, Width: 2*d + 7, Height: h}
}

// corridor is the corridor's floor serving slots standard room pairs.
func (f wingFrame) corridor(slots int32) Rectangle {
	z, h := f.span(2, slots*(f.size[0]+1)+1)
	return Rectangle{X: f.cx - SpineWidth/2, Z: z, Width: SpineWidth, Height: h}
}

// wingReserve is the ground w keeps: a bedroom wing's rooms out to its end
// wall, and no more; suites reserve want at their largest size.
func wingReserve(w Wing, want int) Rectangle {
	if w.Purpose == WingSuites {
		return suiteReserve(w, want)
	}
	f := frameOf(w)
	return f.ground(f.sign*(f.reach(w.Rooms).Z-f.z0)-1, f.size[1])
}

// carve takes r out of the core candidates.
func (g coreGrid) carve(r Rectangle) {
	for _, c := range RectangleCells(r) {
		delete(g.core, c)
	}
}

// bedroomWings is the indexes of the bedroom wings, in order.
func bedroomWings(wings []Wing) []int {
	var out []int
	for i, w := range wings {
		if w.Purpose == WingBedrooms {
			out = append(out, i)
		}
	}
	return out
}

// carveBedroomWings takes every bedroom wing's ground, active and
// retiring, out of g.
func (g coreGrid) carveBedroomWings(wings []Wing) {
	for _, w := range wings {
		if w.Purpose == WingBedrooms || w.Purpose == WingBedroomsRetiring {
			g.carve(wingReserve(w, 0))
		}
	}
}

// wingOf is the index of the wing for purpose, or -1.
func wingOf(wings []Wing, purpose WingPurpose) int {
	for i, w := range wings {
		if w.Purpose == purpose {
			return i
		}
	}
	return -1
}

// growWings sites bedroom wings until they hold pawns rooms (#1950): each
// new wing goes off the main hallway (the column nearest the storage
// room's door, #1178, among those fitting the most rooms) planned at
// wingMaxRooms with tier's room size (#1214). Existing wings are never
// touched. g is the core before any bedroom wing ground is carved out of
// it. Rooms that do not fit are left out.
func (g coreGrid) growWings(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, pawns int, tier BuildTier) ([]SpineSegment, []Wing) {
	if len(spine) == 0 || !alongX(spine[0]) {
		return spine, wings
	}
	owed := pawns
	for _, i := range bedroomWings(wings) {
		owed -= len(wings[i].Rooms)
	}
	for owed > 0 {
		o := g.clone()
		o.carveBedroomWings(wings)
		fits := o.wingFits(spine, rooms)
		size := WingRoomSize(tier)
		f, ok := o.siteWing(spine, rooms, func(try wingFrame) int {
			try.size = size
			n := 0
			for n < wingMaxRooms && fits(try, n) {
				n++
			}
			return n
		})
		if !ok {
			break
		}
		f.size = size
		grown := openWing(spine, f)
		next := o.planWing(grown, rooms, wings, f, fits)
		if len(next) == len(wings) {
			break
		}
		spine, wings = grown, next
		owed -= len(wings[len(wings)-1].Rooms)
	}
	return spine, wings
}

// wingFits is whether a wing framed f fits its k-th room and the corridor
// serving it on g.
func (g coreGrid) wingFits(spine []SpineSegment, rooms []LayoutRoom) func(wingFrame, int) bool {
	free := g.wingGround(spine, rooms)
	return func(f wingFrame, k int) bool {
		return free(roomWalls(f.room(k))) && free(f.corridor(int32(k/2)+1))
	}
}

// clone is a copy of g whose core can be carved on its own.
func (g coreGrid) clone() coreGrid {
	c := g
	c.core = make(map[domain.Cell]bool, len(g.core))
	for k, v := range g.core {
		c.core[k] = v
	}
	return c
}

// wingGround is whether a rectangle is core ground clear of rooms' walls
// and of the hallways' bands (a crossing keeps a room's depth each side).
func (g coreGrid) wingGround(spine []SpineSegment, rooms []LayoutRoom) func(Rectangle) bool {
	taken := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range RectangleCells(roomWalls(r)) {
			taken[c] = true
		}
	}
	half := SpineWidth/2 + coreMaxDepth + 2
	for i, s := range spine {
		band := spineRects([]SpineSegment{s})[0]
		if i > 0 {
			band.Z, band.Height = band.Z-half, band.Height+2*half
		}
		for _, c := range RectangleCells(band) {
			taken[c] = true
		}
	}
	return func(r Rectangle) bool {
		for _, c := range RectangleCells(r) {
			if !g.core[c] || taken[c] {
				return false
			}
		}
		return true
	}
}

// siteWing picks a new wing's frame off the main hallway: among columns
// the hallway can reach, the one fitting the most rooms by count, then over the
// least field soil, then the nearest the storage room's door (#1178).
func (g coreGrid) siteWing(spine []SpineSegment, rooms []LayoutRoom, count func(wingFrame) int) (wingFrame, bool) {
	walls := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range RectangleCells(roomWalls(r)) {
			walls[c] = true
		}
	}
	main := spine[0]
	z0 := main.From.Z
	anchor, ok := wingAnchor(rooms)
	if !ok {
		anchor = domain.Cell{X: (main.From.X + main.To.X) / 2, Z: z0}
	}
	reaches := func(cx int32) bool {
		for x := min(cx-1, main.From.X); x <= max(cx+1, main.To.X); x++ {
			for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
				c := domain.Cell{X: x, Z: z0 + dz}
				if !g.core[c] || walls[c] {
					return false
				}
			}
		}
		return true
	}
	var f wingFrame
	bestN, bestCost, bestD, found := 0, 0, int64(0), false
	for cx := main.From.X - spineMaxLen/2; cx <= main.To.X+spineMaxLen/2; cx++ {
		if !reaches(cx) {
			continue
		}
		for _, sign := range []int32{1, -1} {
			try := wingFrame{cx: cx, z0: z0, sign: sign}
			n := count(try)
			if n == 0 {
				continue
			}
			dx, dz := int64(cx-anchor.X), int64(try.z(2)-anchor.Z)
			d := dx*dx + dz*dz
			// Among columns fitting as many rooms, the one over the least
			// field soil, then the nearest the storage door.
			cost := g.soilCost(try.ground(int32(n), coreMaxDepth))
			if !found || n > bestN || n == bestN && (cost < bestCost || cost == bestCost && d < bestD) {
				f, bestN, bestCost, bestD, found = try, n, cost, d, true
			}
		}
	}
	return f, found
}

// openWing stretches the main hallway to reach f's corridor.
func openWing(spine []SpineSegment, f wingFrame) []SpineSegment {
	spine = append([]SpineSegment(nil), spine...)
	spine[0].From.X, spine[0].To.X = min(spine[0].From.X, f.cx-1), max(spine[0].To.X, f.cx+1)
	return spine
}

// planWing appends a wing framed f with up to wingMaxRooms standard rooms
// to wings; the wing is left out when no room fits.
func (g coreGrid) planWing(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, f wingFrame, fits func(wingFrame, int) bool) []Wing {
	base := domain.Cell{X: f.cx, Z: f.z(2)}
	w := Wing{Purpose: WingBedrooms, Corridor: SpineSegment{From: base, To: base}}
	for k := 0; k < wingMaxRooms && fits(f, k); k++ {
		r := f.room(k)
		r.Dug = g.dug(r)
		trial := w
		trial.Rooms = append(append([]LayoutRoom(nil), w.Rooms...), r)
		trial.Corridor.To = f.reach(trial.Rooms)
		next := append(append([]Wing(nil), wings...), trial)
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Entrances: spineEntrances(spine), Rooms: rooms, Wings: next}); err != nil {
			break
		}
		w = trial
	}
	if len(w.Rooms) == 0 {
		return wings
	}
	return append(append([]Wing(nil), wings...), w)
}

// keepWingRooms keeps the rooms of each wing that keep accepts, shortening
// the corridor to the last kept room; a wing left empty is dropped.
func keepWingRooms(wings []Wing, keep func(LayoutRoom) bool) []Wing {
	var out []Wing
	for _, w := range wings {
		var rooms []LayoutRoom
		for _, r := range w.Rooms {
			if keep(r) {
				rooms = append(rooms, r)
			}
		}
		if len(rooms) == 0 {
			continue
		}
		w.Rooms = rooms
		w.Corridor.To = frameOf(w).reach(rooms)
		out = append(out, w)
	}
	return out
}
