package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Bedroom wings (#1213, epic #1200). The standard bedrooms form one wing: a
// SpineWidth corridor branching off the main hallway, with identical rooms
// on both sides. Neighbouring rooms on a side share their walls and every
// door sits in the corridor wall, so no room is a thoroughfare. A new pawn
// gets the next room at the open end; existing rooms never move.

// WingPurpose is what a wing's rooms are for.
type WingPurpose string

// WingBedrooms is the standard bedroom wing.
const WingBedrooms WingPurpose = "bedrooms"

// Wing is a corridor off the main hallway with its rooms. Corridor.From is
// its centre cell in the main hallway's wall row, Corridor.To the open end.
type Wing struct {
	Purpose  WingPurpose
	Corridor SpineSegment
	Rooms    []LayoutRoom
}

// wingRoomSize is a standard room's interior: width along the corridor,
// depth away from it. A placeholder until sizing by tier (#1214).
var wingRoomSize = [2]int32{3, 4}

// wingGrowthReserve is how many rooms past the wanted count the wing keeps
// ground for at its open end, so other rooms do not block it.
const wingGrowthReserve = 8

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
type wingFrame struct{ cx, z0, sign int32 }

func frameOf(w Wing) wingFrame {
	sign := int32(1)
	if w.Corridor.To.Z < w.Corridor.From.Z {
		sign = -1
	}
	return wingFrame{cx: w.Corridor.From.X, z0: w.Corridor.From.Z - 2*sign, sign: sign}
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
	w, d := wingRoomSize[0], wingRoomSize[1]
	v0 := 3 + int32(k/2)*(w+1)
	z, h := f.span(v0, w)
	door := domain.Cell{Z: f.z(v0 + w/2)}
	r := LayoutRoom{Role: ModuleBedroom}
	if k%2 == 0 {
		r.Interior = Rectangle{X: f.cx + 3, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx+2, domain.West
	} else {
		r.Interior = Rectangle{X: f.cx - 2 - d, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx-2, domain.East
	}
	r.Door = door
	return r
}

// end is the corridor's open end once it serves slots room pairs.
func (f wingFrame) end(slots int32) domain.Cell {
	return domain.Cell{X: f.cx, Z: f.z(2 + slots*(wingRoomSize[0]+1))}
}

// slots is how many room pairs the corridor to c serves.
func (f wingFrame) slots(c domain.Cell) int32 {
	return (f.sign*(c.Z-f.z0) - 2) / (wingRoomSize[0] + 1)
}

// area is the ground slots room pairs take, walls included.
func (f wingFrame) area(slots int32) Rectangle {
	d := wingRoomSize[1]
	z, h := f.span(2, slots*(wingRoomSize[0]+1)+1)
	return Rectangle{X: f.cx - 3 - d, Z: z, Width: 2*d + 7, Height: h}
}

// corridor is the corridor's floor serving slots room pairs.
func (f wingFrame) corridor(slots int32) Rectangle {
	z, h := f.span(2, slots*(wingRoomSize[0]+1)+1)
	return Rectangle{X: f.cx - SpineWidth/2, Z: z, Width: SpineWidth, Height: h}
}

// wingReserve is the ground w keeps for want rooms plus its growth reserve.
func wingReserve(w Wing, want int) Rectangle {
	n := max(want, len(w.Rooms)) + wingGrowthReserve
	return frameOf(w).area(int32((n + 1) / 2))
}

// carve takes r out of the core candidates.
func (g coreGrid) carve(r Rectangle) {
	for _, c := range RectangleCells(r) {
		delete(g.core, c)
	}
}

// bedroomWing is the index of plan's bedroom wing, or -1.
func bedroomWing(wings []Wing) int {
	for i, w := range wings {
		if w.Purpose == WingBedrooms {
			return i
		}
	}
	return -1
}

// growWing sites the bedroom wing off the main hallway if there is none
// (the column nearest the storage room's door, #1178, among those fitting
// the most rooms) and extends it at its open end to pawns rooms. g is the
// core before any wing ground is carved out of it. Rooms that do not fit
// are left out.
func (g coreGrid) growWing(spine []SpineSegment, rooms []LayoutRoom, wings []Wing, pawns int) ([]SpineSegment, []Wing) {
	if len(spine) == 0 || !alongX(spine[0]) {
		return spine, wings
	}
	walls := map[domain.Cell]bool{}
	for _, r := range rooms {
		for _, c := range RectangleCells(roomWalls(r)) {
			walls[c] = true
		}
	}
	taken := map[domain.Cell]bool{}
	for c := range walls {
		taken[c] = true
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
	free := func(r Rectangle) bool {
		for _, c := range RectangleCells(r) {
			if !g.core[c] || taken[c] {
				return false
			}
		}
		return true
	}
	fits := func(f wingFrame, k int) bool {
		return free(roomWalls(f.room(k))) && free(f.corridor(int32(k/2)+1))
	}
	wings = append([]Wing(nil), wings...)
	idx := bedroomWing(wings)
	var f wingFrame
	if idx < 0 {
		if pawns < 1 {
			return spine, wings
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
		bestN, bestD, found := 0, int64(0), false
		for cx := main.From.X - spineMaxLen/2; cx <= main.To.X+spineMaxLen/2; cx++ {
			if !reaches(cx) {
				continue
			}
			for _, sign := range []int32{1, -1} {
				try := wingFrame{cx: cx, z0: z0, sign: sign}
				n := 0
				for n < pawns && fits(try, n) {
					n++
				}
				if n == 0 {
					continue
				}
				dx, dz := int64(cx-anchor.X), int64(try.z(2)-anchor.Z)
				d := dx*dx + dz*dz
				if !found || n > bestN || n == bestN && d < bestD {
					f, bestN, bestD, found = try, n, d, true
				}
			}
		}
		if !found {
			return spine, wings
		}
		spine = append([]SpineSegment(nil), spine...)
		spine[0].From.X, spine[0].To.X = min(main.From.X, f.cx-1), max(main.To.X, f.cx+1)
		base := domain.Cell{X: f.cx, Z: f.z(2)}
		wings = append(wings, Wing{Purpose: WingBedrooms, Corridor: SpineSegment{From: base, To: base}})
		idx = len(wings) - 1
	} else {
		f = frameOf(wings[idx])
	}
	w := wings[idx]
	w.Rooms = append([]LayoutRoom(nil), w.Rooms...)
	have := map[Rectangle]bool{}
	last := -1
	for k := 0; len(have) < len(w.Rooms) && k < 4096; k++ {
		if r := f.room(k); containsRoom(w.Rooms, r.Interior) {
			have[r.Interior], last = true, k
		}
	}
	slots := int32(0)
	if len(w.Rooms) > 0 {
		slots = f.slots(w.Corridor.To)
	}
	for k := 0; len(w.Rooms) < pawns && k < 4096; k++ {
		r := f.room(k)
		if have[r.Interior] {
			continue
		}
		if !fits(f, k) {
			if k < last {
				continue
			}
			break
		}
		r.Dug = g.dug(r)
		trial := w
		trial.Rooms = append(append([]LayoutRoom(nil), w.Rooms...), r)
		trial.Corridor.To = f.end(max(slots, int32(k/2)+1))
		next := append([]Wing(nil), wings...)
		next[idx] = trial
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Rooms: rooms, Wings: next}); err != nil {
			break
		}
		w, slots = trial, max(slots, int32(k/2)+1)
	}
	if len(w.Rooms) == 0 {
		return spine, append(wings[:idx], wings[idx+1:]...)
	}
	wings[idx] = w
	return spine, wings
}

func containsRoom(rooms []LayoutRoom, in Rectangle) bool {
	for _, r := range rooms {
		if r.Interior == in {
			return true
		}
	}
	return false
}

// keepWingRooms keeps the rooms of each wing that keep accepts, shortening
// the corridor to the last kept room; a wing left empty is dropped.
func keepWingRooms(wings []Wing, keep func(LayoutRoom) bool) []Wing {
	var out []Wing
	for _, w := range wings {
		f := frameOf(w)
		var rooms []LayoutRoom
		slots := int32(0)
		for _, r := range w.Rooms {
			if !keep(r) {
				continue
			}
			rooms = append(rooms, r)
			v := r.Interior.Z - f.z0
			if f.sign < 0 {
				v = f.z0 - (r.Interior.Z + r.Interior.Height - 1)
			}
			slots = max(slots, (v-3)/(wingRoomSize[0]+1)+1)
		}
		if len(rooms) == 0 {
			continue
		}
		w.Rooms = rooms
		w.Corridor.To = f.end(slots)
		out = append(out, w)
	}
	return out
}
