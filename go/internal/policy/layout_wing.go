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
func retireWings(wings []Wing, tier TechTier) []Wing {
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
	Rooms    []PlannedRoom
}

// WingRoomSize is a standard room's interior for tier (#1214, epic #1200):
// width along the corridor, depth away from it. Camp and Masonry rooms are
// 3x4, Powered and Industrial 4x4, Spacer 4x5. A new wing takes the
// current tier's size; an existing wing keeps the size of its rooms.
func WingRoomSize(tier TechTier) [2]int32 {
	switch {
	case tier >= TechTierSpacer:
		return [2]int32{4, 5}
	case tier >= TechTierPowered:
		return [2]int32{4, 4}
	}
	return [2]int32{3, 4}
}

// wingMaxRooms is a bedroom wing's size (#1950, epic #1938): a wing is
// planned at this many rooms when sited and never grows; once every wing
// is occupied, a new one is sited.
const wingMaxRooms = 10

// AllRooms is every planned room: the spine's, then each wing's.
func (p LayoutPlan) AllRooms() []PlannedRoom {
	if len(p.Wings) == 0 {
		return p.Rooms
	}
	out := append([]PlannedRoom(nil), p.Rooms...)
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
//
// A wing runs along Z off an east-west hallway by default; horiz transposes
// it (#1965): the corridor runs along X at row cx off a north-south hallway
// at column z0, v counting cells east (sign +1) or west (-1). The geometry
// is computed in the Z-axis frame and transposed, so the two are twins.
type wingFrame struct {
	cx, z0, sign int32
	size         [2]int32
	horiz        bool
}

// frameOf is w's frame; a standard wing's rooms keep the size of its first.
// A wing is horizontal when its corridor runs along X.
func frameOf(w Wing) wingFrame {
	c := w.Corridor
	f := wingFrame{sign: 1, size: WingRoomSize(TechTierCamp)}
	if (w.Purpose == WingBedrooms || w.Purpose == WingBedroomsRetiring) && len(w.Rooms) > 0 {
		in := w.Rooms[0].Interior
		f.size = [2]int32{in.Height, in.Width}
		if c.From.X != c.To.X {
			f.size = [2]int32{in.Width, in.Height}
		}
	}
	if c.From.X != c.To.X {
		f.horiz = true
		if c.To.X < c.From.X {
			f.sign = -1
		}
		f.cx, f.z0 = c.From.Z, c.From.X-2*f.sign
		return f
	}
	if c.To.Z < c.From.Z {
		f.sign = -1
	}
	f.cx, f.z0 = c.From.X, c.From.Z-2*f.sign
	return f
}

// orient moves a cell from the Z-axis frame to f's.
func (f wingFrame) orient(c domain.Cell) domain.Cell {
	if f.horiz {
		return transposeCell(c)
	}
	return c
}

// orientRect moves a rectangle from the Z-axis frame to f's.
func (f wingFrame) orientRect(r Rectangle) Rectangle {
	if f.horiz {
		return Rectangle{X: r.Z, Z: r.X, Width: r.Height, Height: r.Width}
	}
	return r
}

// orientRoom moves a room from the Z-axis frame to f's.
func (f wingFrame) orientRoom(r PlannedRoom) PlannedRoom {
	if f.horiz {
		return transposeRoom(r)
	}
	return r
}

// cell is the cell at corridor row v on the corridor's axis.
func (f wingFrame) cell(v int32) domain.Cell { return f.orient(domain.Cell{X: f.cx, Z: f.z(v)}) }

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
func (f wingFrame) room(k int) PlannedRoom {
	w := f.size[0]
	return f.roomAt(k%2 == 0, 3+int32(k/2)*(w+1), w, f.size[1], PlannedBedroom)
}

// roomAt is a role room of interior w along the corridor by d away from
// it, east or west of the corridor over v0..v0+w-1, its door mid-side in
// the corridor wall.
func (f wingFrame) roomAt(east bool, v0, w, d int32, role PlannedRole) PlannedRoom {
	z, h := f.span(v0, w)
	door := domain.Cell{Z: f.z(v0 + w/2)}
	r := PlannedRoom{Role: role}
	if east {
		r.Interior = Rectangle{X: f.cx + 3, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx+2, domain.West
	} else {
		r.Interior = Rectangle{X: f.cx - 2 - d, Z: z, Width: d, Height: h}
		door.X, r.DoorRot = f.cx-2, domain.East
	}
	r.Door = door
	return f.orientRoom(r)
}

// along is r's first cell out along the corridor (v0) and its width there.
func (f wingFrame) along(r PlannedRoom) (int32, int32) {
	r = f.orientRoom(r)
	if f.sign > 0 {
		return r.Interior.Z - f.z0, r.Interior.Height
	}
	return f.z0 - (r.Interior.Z + r.Interior.Height - 1), r.Interior.Height
}

// reach is the corridor's open end serving rooms: the wall row past the
// farthest one.
func (f wingFrame) reach(rooms []PlannedRoom) domain.Cell { return f.cell(f.reachV(rooms)) }

// reachV is the wall row of reach, counted from the hallway.
func (f wingFrame) reachV(rooms []PlannedRoom) int32 {
	v := int32(2)
	for _, r := range rooms {
		v0, w := f.along(r)
		v = max(v, v0+w)
	}
	return v
}

// ground is the wing's ground out to v=2+n-1 with rooms depth d each
// side, walls included.
func (f wingFrame) ground(n, d int32) Rectangle {
	z, h := f.span(2, n)
	return f.orientRect(Rectangle{X: f.cx - 3 - d, Z: z, Width: 2*d + 7, Height: h})
}

// corridor is the corridor's floor serving slots standard room pairs.
func (f wingFrame) corridor(slots int32) Rectangle {
	return f.corridorTo(slots*(f.size[0]+1) + 2)
}

// wingReserve is the ground w keeps: a bedroom wing's rooms out to its end
// wall, and no more; suites reserve the ground out to their deepest suite.
func wingReserve(w Wing) Rectangle {
	if w.Purpose == WingSuites {
		return suiteWingGround(w)
	}
	f := frameOf(w)
	return f.ground(f.reachV(w.Rooms)-1, f.size[1])
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
			g.carve(wingReserve(w))
		}
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

// planWing appends a wing framed f with up to wingMaxRooms standard rooms
// to wings; the wing is left out when no room fits.
func (g coreGrid) planWing(spine []SpineSegment, rooms []PlannedRoom, wings []Wing, f wingFrame, fits func(wingFrame, int) bool) []Wing {
	base := f.cell(2)
	w := Wing{Purpose: WingBedrooms, Corridor: SpineSegment{From: base, To: base}}
	for k := 0; k < wingMaxRooms && fits(f, k); k++ {
		r := f.room(k)
		r.Dug = g.dug(r)
		trial := w
		trial.Rooms = append(append([]PlannedRoom(nil), w.Rooms...), r)
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
func keepWingRooms(wings []Wing, keep func(PlannedRoom) bool) []Wing {
	var out []Wing
	for _, w := range wings {
		var rooms []PlannedRoom
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
