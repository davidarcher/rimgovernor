package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Core spine and room slots (#779, A3). The core is one straight 3-wide
// hallway along X with rooms hung off both sides. Neighbouring rooms on a
// side share their side walls; every room's door sits in its hallway wall,
// so no room is a thoroughfare. The core takes cells only from the core
// candidates (ZoneCore); a room over rock (ZoneMining) is Dug. The other
// zones give way to the core wherever they overlap it.

// Room roles the v2 core adds beside the master-plan ones; the jail is
// ModulePrison.
const (
	ModuleBedroom  ModuleRole = "bedroom"
	ModuleBarracks ModuleRole = "barracks"
	ModuleDining   ModuleRole = "dining"
	ModuleRec      ModuleRole = "rec"
	ModuleLab      ModuleRole = "lab"
	// ModuleTomb is the sarcophagus room (#832), shelled only once a
	// colonist lies dead.
	ModuleTomb ModuleRole = "tomb"
)

// coreRoomSize is a role's interior: width along the spine, depth away
// from it. Bedrooms are 5x5 (25 cells, the very-impressive floor).
var coreRoomSize = map[ModuleRole][2]int32{
	ModuleBedroom:  {5, 5},
	ModuleBarracks: {7, 5},
	ModuleKitchen:  {6, 5},
	ModuleFreezer:  {5, 5},
	ModuleDining:   {9, 7},
	ModuleRec:      {9, 7},
	ModuleHospital: {7, 5},
	ModulePrison:   {5, 5},
	ModuleWorkshop: {7, 5},
	ModuleStorage:  {9, 7},
	ModuleLab:      {6, 5},
	ModuleTomb:     {5, 5},
}

// coreBaseRooms is every colony's fixed set, in placement order: pairs
// that trade goods sit side by side.
var coreBaseRooms = []ModuleRole{
	ModuleBarracks, ModuleKitchen, ModuleFreezer, ModuleDining, ModuleRec,
	ModuleWorkshop, ModuleStorage, ModuleHospital, ModulePrison, ModuleLab,
	ModuleTomb,
}

// coreMaxDepth is the deepest interior, which bounds the core's cross-section.
const coreMaxDepth int32 = 7

// PlanCore lays a fresh spine through the core candidates in zones and
// rooms for pawns colonists.
func PlanCore(zones []LayoutZone, pawns int) LayoutPlan {
	return Grow(LayoutPlan{Zones: zones}, pawns)
}

// Grow adds whatever rooms plan lacks for pawns colonists (the base set,
// then one bedroom each) by extending the spine; existing rooms never move.
// A plan with no spine gets one near the core candidates' centre. Rooms
// that no longer fit are left out.
func Grow(plan LayoutPlan, pawns int) LayoutPlan {
	g := newCoreGrid(plan.Zones, plan.Reservations)
	if len(g.core) == 0 {
		return plan
	}
	if len(plan.Spine) == 0 {
		start, ok := g.seed()
		if !ok {
			return plan
		}
		plan.Spine = []SpineSegment{{From: start, To: start}}
	}
	have := map[ModuleRole]int{}
	for _, r := range plan.Rooms {
		have[r.Role]++
	}
	var want []ModuleRole
	for _, role := range coreBaseRooms {
		if have[role] == 0 {
			want = append(want, role)
		}
	}
	for i := have[ModuleBedroom]; i < pawns; i++ {
		want = append(want, ModuleBedroom)
	}
	rooms := append([]LayoutRoom(nil), plan.Rooms...)
	seg := plan.Spine[0]
	for _, role := range want {
		next := seg
		room, ok := g.besideKitchen(&next, rooms, role)
		if !ok {
			room, ok = g.place(&next, rooms, role)
		}
		if !ok {
			break
		}
		// A room that makes a thoroughfare (#780) is left out; the next
		// role tries the following slot.
		trial := append(append([]LayoutRoom(nil), rooms...), room)
		if _, err := CheckRoutes(LayoutPlan{Spine: []SpineSegment{next}, Rooms: trial}); err != nil {
			continue
		}
		seg, rooms = next, trial
	}
	spine := append([]SpineSegment{seg}, plan.Spine[1:]...)
	plan.Spine, plan.Rooms = spine, rooms
	return plan
}

type coreGrid struct {
	core, rock map[domain.Cell]bool
}

// newCoreGrid takes reserved sites out of the core candidates.
func newCoreGrid(zones []LayoutZone, reserved []LayoutReservation) coreGrid {
	g := coreGrid{core: map[domain.Cell]bool{}, rock: map[domain.Cell]bool{}}
	for _, z := range zones {
		var set map[domain.Cell]bool
		switch z.Kind {
		case ZoneCore:
			set = g.core
		case ZoneMining:
			set = g.rock
		default:
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				set[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	for _, r := range reserved {
		for x := r.Area.X; x < r.Area.X+r.Area.Width; x++ {
			for z := r.Area.Z; z < r.Area.Z+r.Area.Height; z++ {
				delete(g.core, domain.Cell{X: x, Z: z})
			}
		}
	}
	return g
}

// column reports the whole core cross-section at x around spine row z:
// hallway plus the deepest room and its walls on both sides.
func (g coreGrid) column(x, z int32) bool {
	half := SpineWidth/2 + coreMaxDepth + 2
	for dz := -half; dz <= half; dz++ {
		if !g.core[domain.Cell{X: x, Z: z + dz}] {
			return false
		}
	}
	return true
}

// seed picks the spine start: the column-fitting cell nearest the core
// candidates' centroid.
func (g coreGrid) seed() (domain.Cell, bool) {
	var sx, sz int64
	for c := range g.core {
		sx += int64(c.X)
		sz += int64(c.Z)
	}
	n := int64(len(g.core))
	cx, cz := int32(sx/n), int32(sz/n)
	best, found, bestD := domain.Cell{}, false, int64(-1)
	for c := range g.core {
		dx, dz := int64(c.X-cx), int64(c.Z-cz)
		d := dx*dx + dz*dz
		if found && (d > bestD || d == bestD && (c.Z > best.Z || c.Z == best.Z && c.X > best.X)) {
			continue
		}
		if g.column(c.X, c.Z) {
			best, found, bestD = c, true, d
		}
	}
	return best, found
}

// place puts role's room in the next slot east of the rooms (then west),
// extending seg over it.
func (g coreGrid) place(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole) (LayoutRoom, bool) {
	return g.placeSized(seg, rooms, role, coreRoomSize[role])
}

func (g coreGrid) placeSized(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole, size [2]int32) (LayoutRoom, bool) {
	w, d := size[0], size[1]
	z0 := seg.From.Z
	// Wall rows: the hallway spans z0-1..z0+1, so side walls start at z0±2.
	for _, east := range []bool{true, false} {
		best := LayoutRoom{}
		found := false
		for _, north := range []bool{true, false} {
			// Walls on this side: the next slot's west wall is the last
			// room's east wall (shared).
			edge, any := seg.From.X-1, false
			for _, r := range rooms {
				if (r.Interior.Z > z0) != north {
					continue
				}
				if east && (!any || r.Interior.X+r.Interior.Width > edge) {
					edge = r.Interior.X + r.Interior.Width
				}
				if !east && (!any || r.Interior.X-1 < edge) {
					edge = r.Interior.X - 1
				}
				any = true
			}
			for shift := int32(0); shift < 64; shift++ {
				var ix int32
				if east {
					ix = edge + 1 + shift
				} else {
					ix = edge - w - shift
				}
				room := coreRoom(role, ix, z0, w, d, north)
				if !g.fits(room, *seg) {
					continue
				}
				if !found || (east && room.Interior.X < best.Interior.X) || (!east && room.Interior.X > best.Interior.X) {
					best, found = room, true
				}
				break
			}
		}
		if found {
			best.Dug = g.dug(best)
			lo, hi := best.Interior.X-1, best.Interior.X+best.Interior.Width
			if lo < seg.From.X {
				seg.From.X = lo
			}
			if hi > seg.To.X {
				seg.To.X = hi
			}
			return best, true
		}
	}
	return LayoutRoom{}, false
}

func coreRoom(role ModuleRole, ix, z0, w, d int32, north bool) LayoutRoom {
	door := domain.Cell{X: ix + w/2}
	r := LayoutRoom{Role: role}
	if north {
		door.Z = z0 + 2
		r.Interior = Rectangle{X: ix, Z: z0 + 3, Width: w, Height: d}
		r.DoorRot = domain.South
	} else {
		door.Z = z0 - 2
		r.Interior = Rectangle{X: ix, Z: z0 - 2 - d, Width: w, Height: d}
		r.DoorRot = domain.North
	}
	r.Door = door
	return r
}

// fits reports the room with its walls, and the hallway beside it, all on
// core candidates.
func (g coreGrid) fits(r LayoutRoom, seg SpineSegment) bool {
	in := r.Interior
	for x := in.X - 1; x <= in.X+in.Width; x++ {
		for z := in.Z - 1; z <= in.Z+in.Height; z++ {
			if !g.core[domain.Cell{X: x, Z: z}] {
				return false
			}
		}
		for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
			if !g.core[domain.Cell{X: x, Z: seg.From.Z + dz}] {
				return false
			}
		}
	}
	// The hallway between the spine and this room must be continuous.
	lo, hi := in.X-1, in.X+in.Width
	if hi < seg.From.X {
		lo, hi = hi, seg.From.X
	} else if lo > seg.To.X {
		lo, hi = seg.To.X, lo
	} else {
		return true
	}
	for x := lo; x <= hi; x++ {
		for dz := -SpineWidth / 2; dz <= SpineWidth/2; dz++ {
			if !g.core[domain.Cell{X: x, Z: seg.From.Z + dz}] {
				return false
			}
		}
	}
	return true
}

func (g coreGrid) dug(r LayoutRoom) bool {
	in := r.Interior
	for x := in.X; x < in.X+in.Width; x++ {
		for z := in.Z; z < in.Z+in.Height; z++ {
			if g.rock[domain.Cell{X: x, Z: z}] {
				return true
			}
		}
	}
	return false
}
