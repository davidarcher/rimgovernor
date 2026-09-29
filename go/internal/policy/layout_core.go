package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Core spine and room slots (#779, A3). The core is a straight 3-wide main
// hallway along X, crossed by north-south hallways as it fills (#952,
// layout_spines.go), with rooms hung off both sides. Neighbouring rooms on a
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
// from it. Bedrooms live in the wing (layout_wing.go).
var coreRoomSize = map[ModuleRole][2]int32{
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
func PlanCore(zones []LayoutZone, pawns int, tier BuildTier) LayoutPlan {
	return Grow(LayoutPlan{Zones: zones}, pawns, 1, tier)
}

// Grow adds whatever rooms plan lacks for pawns colonists (the base set,
// then the bedroom wing to one room each, then tomb rooms up to tombs, #857) by extending the
// spine, and a meal closet behind the dining room when no freezer opens
// into it (#936); existing rooms never move.
// A plan with no spine gets one near the core candidates' centre. Rooms
// that no longer fit are left out. A new bedroom wing takes tier's room
// size (#1214).
// suites are the impressiveness targets of the suites wanted, in order;
// the suite wing is sited and grown to one suite each (#1215); SuiteTargets
// builds it from the pawns SuiteClaims finds (#1216), and none keeps a
// plan's existing suite wing as it is.
func Grow(plan LayoutPlan, pawns, tombs int, tier BuildTier, suites ...float64) LayoutPlan {
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
	// The bedroom wing is sited right behind the storage room, so it takes
	// the ground beside it before the later base rooms do (#1178).
	var want []ModuleRole
	for _, role := range coreBaseRooms {
		if have[role] == 0 || role == ModuleStorage {
			want = append(want, role)
		}
	}
	for i := max(have[ModuleTomb], 1); i < tombs; i++ {
		want = append(want, ModuleTomb)
	}
	rooms := append([]LayoutRoom(nil), plan.Rooms...)
	spine := append([]SpineSegment(nil), plan.Spine...)
	wings := plan.Wings
	// Other rooms stay off the wing's ground and its growth reserve.
	base := newCoreGrid(plan.Zones, plan.Reservations)
	if i := wingOf(wings, WingSuites); i >= 0 {
		g.carve(wingReserve(wings[i], len(suites)))
		base.carve(wingReserve(wings[i], len(suites)))
	}
	if i := bedroomWing(wings); i >= 0 {
		g.carve(wingReserve(wings[i], pawns))
	}
	if len(spine) == 1 {
		// The centre crossing is laid first so no room takes its column (#952).
		if next, ok := g.addCrossing(spine, rooms); ok {
			spine = next
		}
	}
	for _, role := range want {
		if role == ModuleStorage && have[role] > 0 {
			spine, wings = growWing(g, base, spine, rooms, wings, pawns, tier)
			continue
		}
		placed, fit := false, false
		for !placed {
			for i := range spine {
				var next SpineSegment
				var room LayoutRoom
				ok := false
				if i == 0 {
					local, main, _ := g.segmentGrid(spine, 0, rooms)
					next = main
					room, ok = local.beside(&next, rooms, role)
				}
				if !ok {
					next, room, ok = g.placeOn(spine, i, rooms, role, coreRoomSize[role])
				}
				if !ok {
					continue
				}
				fit = true
				// A room that makes a thoroughfare (#780) is left out; the
				// next hallway (or role) tries its slot.
				trial := append(append([]LayoutRoom(nil), rooms...), room)
				grown := append([]SpineSegment(nil), spine...)
				grown[i] = next
				if _, err := CheckRoutes(LayoutPlan{Spine: grown, Rooms: trial, Wings: wings}); err != nil {
					continue
				}
				spine, rooms, placed = grown, trial, true
				break
			}
			if placed || fit {
				break
			}
			next, ok := g.addCrossing(spine, rooms)
			if !ok {
				break
			}
			spine = next
		}
		if !fit {
			break
		}
		if role == ModuleStorage {
			spine, wings = growWing(g, base, spine, rooms, wings, pawns, tier)
		}
	}
	if closet, ok := g.mealCloset(rooms); ok {
		trial := append(append([]LayoutRoom(nil), rooms...), closet)
		if _, err := CheckRoutes(LayoutPlan{Spine: spine, Rooms: trial, Wings: wings}); err == nil {
			rooms = trial
		}
	}
	// The suite wing grows over its own ground and whatever no other room
	// or the bedroom wing claimed.
	sg := newCoreGrid(plan.Zones, plan.Reservations)
	if i := bedroomWing(wings); i >= 0 {
		sg.carve(wingReserve(wings[i], pawns))
	}
	spine, wings = sg.growSuites(spine, rooms, wings, suites)
	plan.Spine, plan.Rooms, plan.Wings = spine, rooms, wings
	return plan
}

// growWing grows the bedroom wing over base (the core with no wing ground
// carved out) and carves its ground out of g.
func growWing(g, base coreGrid, spine []SpineSegment, rooms []LayoutRoom, wings []Wing, pawns int, tier BuildTier) ([]SpineSegment, []Wing) {
	spine, wings = base.growWing(spine, rooms, wings, pawns, tier)
	if i := bedroomWing(wings); i >= 0 {
		g.carve(wingReserve(wings[i], pawns))
	}
	return spine, wings
}

type coreGrid struct {
	core, rock map[domain.Cell]bool
	// maxLen caps the hallway's length; 0 leaves it open.
	maxLen int32
	// junction, when set, is where the hallway meets another (its X along
	// the hallway): rooms go to whichever end stays nearer it.
	junction    int32
	hasJunction bool
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

// placeSized puts a room of size in the nearest slot at whichever end of
// the rooms keeps seg shortest (or nearest its junction), extending seg
// over it.
func (g coreGrid) placeSized(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole, size [2]int32) (LayoutRoom, bool) {
	w, d := size[0], size[1]
	z0 := seg.From.Z
	// Wall rows: the hallway spans z0-1..z0+1, so side walls start at z0±2.
	// Each end offers its nearest slot; the one that lengthens the hallway
	// least wins (east on a tie), so the hallway grows out from its centre.
	pick, picked, pickGrow := LayoutRoom{}, false, int32(0)
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
		if !found {
			continue
		}
		lo, hi := min(seg.From.X, best.Interior.X-1), max(seg.To.X, best.Interior.X+best.Interior.Width)
		if g.maxLen > 0 && hi-lo > g.maxLen {
			continue
		}
		grow := hi - lo - (seg.To.X - seg.From.X)
		if g.hasJunction {
			grow = max(hi-g.junction, g.junction-lo)
		}
		if !picked || grow < pickGrow {
			pick, picked, pickGrow = best, true, grow
		}
	}
	if !picked {
		return LayoutRoom{}, false
	}
	pick.Dug = g.dug(pick)
	seg.From.X = min(seg.From.X, pick.Interior.X-1)
	seg.To.X = max(seg.To.X, pick.Interior.X+pick.Interior.Width)
	return pick, true
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
