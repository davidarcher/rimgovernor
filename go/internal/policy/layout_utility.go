package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Utility reservations (#782, A6). After the core is laid out the plan
// holds:
//   - a battery room off the spine, a room like any other (ModuleBattery);
//   - wind turbines in facing north/south pairs whose catch zones overlap
//     in one shared lane, every zone cell a field and none roofed, walled,
//     rock or core;
//   - solar on its own clear 4x4 plots;
//   - a stone enclosure around each geyser's generator;
//   - an exhaust cell (or dug shaft) behind each room that needs cooling.
// Turbine, solar and geothermal sites stay out of the core's cross-section
// band so the spine can keep growing along it, and Grow never builds over
// a reservation.

// ModuleBattery is the battery room: 5 wide, a 1-cell walkway from the
// door down the middle, batteries (1x2) along both sides with a stone
// block between each, roofed, stone door.
const ModuleBattery ModuleRole = "battery"

// ReserveExhaust is the cells behind a cooled room's back wall the cooler
// dumps heat into: one open cell, or a shaft dug through rock to open
// ground.
const ReserveExhaust ReservationKind = "cooler_exhaust"

// batteryRoomSize holds four batteries a side.
var batteryRoomSize = [2]int32{5, 7}

const (
	turbineWidth int32 = 7
	// turbinePairSpan is a facing pair from the north-facing turbine's
	// back zone to the south-facing one's: 6 back + 2 + 10 lane + 2 + 6.
	turbinePairSpan int32 = 26
	solarSide       int32 = 4
	geothermalSide  int32 = 6
	// geothermalShell is the enclosure's thickness.
	geothermalShell int32 = 2
	exhaustMax      int32 = 12
)

// coolingRoles are the rooms that need a cooler, most important first;
// the tomb keeps colonist corpses frozen (#840).
var coolingRoles = []ModuleRole{ModuleFreezer, ModuleTomb}

// UtilityWants is what PlanUtilities reserves: turbine pairs, solar plots
// and the steam geysers (each the geyser's 2x2 footprint).
type UtilityWants struct {
	TurbinePairs, Solar int
	Geysers             []Rectangle
}

// PlanUtilities adds a battery room, cooler exhausts and the wanted
// generator sites to plan. Sites that do not fit are left out.
func PlanUtilities(plan LayoutPlan, want UtilityWants) LayoutPlan {
	if len(plan.Spine) == 0 {
		return plan
	}
	plan.Rooms = append([]LayoutRoom(nil), plan.Rooms...)
	plan.Reservations = append([]LayoutReservation(nil), plan.Reservations...)
	plan.Zones = append([]LayoutZone(nil), plan.Zones...)
	has := false
	for _, r := range plan.Rooms {
		has = has || r.Role == ModuleBattery
	}
	if !has {
		g := newCoreGrid(plan.Zones, plan.Reservations)
		seg := plan.Spine[0]
		if room, ok := g.placeSized(&seg, plan.Rooms, ModuleBattery, batteryRoomSize); ok {
			plan.Rooms = append(plan.Rooms, room)
			plan.Spine = append([]SpineSegment{seg}, plan.Spine[1:]...)
		}
	}
	u := newUtilityGrid(plan)
	for _, role := range coolingRoles {
		for _, r := range plan.Rooms {
			if r.Role != role {
				continue
			}
			if area, ok := u.exhaust(r); ok {
				u.reserve(&plan, LayoutReservation{Kind: ReserveExhaust, Area: area})
			}
		}
	}
	for _, gz := range want.Geysers {
		cx, cz := gz.X+gz.Width/2, gz.Z+gz.Height/2
		gen := Rectangle{X: cx - geothermalSide/2, Z: cz - geothermalSide/2, Width: geothermalSide, Height: geothermalSide}
		area := Rectangle{X: gen.X - geothermalShell, Z: gen.Z - geothermalShell, Width: gen.Width + 2*geothermalShell, Height: gen.Height + 2*geothermalShell}
		if u.free(area, true) {
			u.reserve(&plan, LayoutReservation{Kind: ReserveGeothermal, Area: area})
		}
	}
	pair := int32(0)
	for _, r := range plan.Reservations {
		if r.Pair > pair {
			pair = r.Pair
		}
	}
	for i := 0; i < want.TurbinePairs; i++ {
		site, ok := u.site(turbineWidth, turbinePairSpan, false)
		if !ok {
			break
		}
		pair++
		for _, r := range turbinePair(site, pair) {
			u.reserve(&plan, r)
		}
		lane := Rectangle{X: site.X, Z: site.Z, Width: turbineWidth, Height: turbinePairSpan}
		var runs []RowRun
		for z := lane.Z; z < lane.Z+lane.Height; z++ {
			if dz := z - lane.Z; dz == 6 || dz == 7 || dz == 18 || dz == 19 {
				continue // turbine footprints
			}
			runs = append(runs, RowRun{Z: z, X: lane.X, Length: lane.Width})
		}
		plan.Zones = append(plan.Zones, LayoutZone{Kind: ZoneField, Runs: runs})
	}
	for i := 0; i < want.Solar; i++ {
		site, ok := u.site(solarSide, solarSide, false)
		if !ok {
			break
		}
		u.reserve(&plan, LayoutReservation{Kind: ReserveSolar, Area: site})
	}
	return plan
}

// turbinePair lays a facing pair in the 7x26 site: the north-facing
// turbine at rows 6-7, the south-facing one at rows 18-19, and three lanes
// (its back zone, the shared 10-row lane, the other's back zone).
func turbinePair(site Rectangle, pair int32) []LayoutReservation {
	row := func(dz, h int32, kind ReservationKind) LayoutReservation {
		return LayoutReservation{Kind: kind, Area: Rectangle{X: site.X, Z: site.Z + dz, Width: turbineWidth, Height: h}, Pair: pair}
	}
	return []LayoutReservation{
		row(6, 2, ReserveTurbine), row(18, 2, ReserveTurbine),
		row(0, 6, ReserveTurbineLane), row(8, 10, ReserveTurbineLane), row(20, 6, ReserveTurbineLane),
	}
}

// TurbinePlacement is a turbine reservation's native centre and rotation:
// the pair's southern turbine faces north, its northern one south.
func TurbinePlacement(area Rectangle, pairSouth bool) (domain.Cell, domain.Rotation) {
	if pairSouth {
		return domain.Cell{X: area.X + 3, Z: area.Z}, domain.North
	}
	return domain.Cell{X: area.X + 3, Z: area.Z + 1}, domain.South
}

// TurbineWindCells mirrors WindTurbineUtility.CalculateWindCells for a
// 7x2 turbine: 7 wide, 10 rows in front and 6 behind.
func TurbineWindCells(center domain.Cell, rot domain.Rotation) []domain.Cell {
	off, front, back := int32(0), int32(9), int32(5)
	if rot != domain.North && rot != domain.East {
		off, front, back = -1, 5, 9
	}
	var a, b Rectangle // X, Z, Width, Height as min/extent
	if rot == domain.East || rot == domain.West {
		a = Rectangle{X: center.X + 2 + off, Z: center.Z - 3, Width: front + 1, Height: 7}
		b = Rectangle{X: center.X - 1 - back + off, Z: center.Z - 3, Width: back + 1, Height: 7}
	} else {
		a = Rectangle{X: center.X - 3, Z: center.Z + 2 + off, Width: 7, Height: front + 1}
		b = Rectangle{X: center.X - 3, Z: center.Z - 1 - back + off, Width: 7, Height: back + 1}
	}
	var out []domain.Cell
	for _, r := range []Rectangle{a, b} {
		for z := r.Z; z < r.Z+r.Height; z++ {
			for x := r.X; x < r.X+r.Width; x++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

// BatterySlots are a battery room's battery footprints (1x2 across the
// walkway on each side), nearest the door first; the rows between them
// hold the stone blocks.
func BatterySlots(r LayoutRoom) []Rectangle {
	in := r.Interior
	var out []Rectangle
	for _, i := range AisleRows(in.Height, 2) {
		z := in.Z + i
		if r.DoorRot == domain.North { // door on the south wall
			z = in.Z + in.Height - 1 - i
		}
		out = append(out,
			Rectangle{X: in.X, Z: z, Width: 2, Height: 1},
			Rectangle{X: in.X + in.Width - 2, Z: z, Width: 2, Height: 1})
	}
	return out
}

// PlannedPowerSite is one planned battery or generator placement: the
// native centre and rotation, and the footprint the preview must match.
// Block is the stone block between a battery and the one before it on its
// side (zero for the first row and for generators).
type PlannedPowerSite struct {
	Cell     domain.Cell
	Rotation domain.Rotation
	Area     Rectangle
	Block    Rectangle
}

// PlannedPowerSites lists the plan's sites for definition (#788):
// batteries in the battery room's slots (1x2 turned east), wind turbines on
// their pair reservations (the southern one facing north), solar on its
// plots. Nil for any other definition or a plan with no such site.
func PlannedPowerSites(plan LayoutPlan, definition string) []PlannedPowerSite {
	var out []PlannedPowerSite
	switch definition {
	case BatteryDefinition:
		for _, r := range plan.Rooms {
			if r.Role != ModuleBattery {
				continue
			}
			slots := BatterySlots(r)
			for i, s := range slots {
				site := PlannedPowerSite{Cell: domain.Cell{X: s.X, Z: s.Z}, Rotation: domain.East, Area: s}
				if i >= 2 {
					prev := slots[i-2]
					site.Block = Rectangle{X: s.X, Z: (s.Z + prev.Z) / 2, Width: s.Width, Height: 1}
				}
				out = append(out, site)
			}
		}
	case WindTurbineDefinition:
		for _, r := range plan.Reservations {
			if r.Kind != ReserveTurbine {
				continue
			}
			south := true
			for _, o := range plan.Reservations {
				if o.Kind == ReserveTurbine && o.Pair == r.Pair && o.Area.Z < r.Area.Z {
					south = false
				}
			}
			c, rot := TurbinePlacement(r.Area, south)
			out = append(out, PlannedPowerSite{Cell: c, Rotation: rot, Area: r.Area})
		}
	case SolarDefinition:
		for _, r := range plan.Reservations {
			if r.Kind == ReserveSolar {
				out = append(out, PlannedPowerSite{Cell: domain.Cell{X: r.Area.X + 1, Z: r.Area.Z + 1}, Rotation: domain.North, Area: r.Area})
			}
		}
	case GeothermalDefinition:
		// The 6x6 generator inside the shell, on the geyser's position: a
		// 6-wide footprint spans its centre -2..+3.
		for _, r := range plan.Reservations {
			if r.Kind == ReserveGeothermal {
				gen := pad(r.Area, -geothermalShell)
				out = append(out, PlannedPowerSite{Cell: domain.Cell{X: gen.X + 2, Z: gen.Z + 2}, Rotation: domain.North, Area: gen})
			}
		}
	}
	return out
}

// PlannedCoolerSite is a cooled room's planned cooler (#791): the cell in
// its back wall in front of the exhaust reservation, turned so the hot side
// faces the exhaust and the cold side the room.
type PlannedCoolerSite struct {
	Cell     domain.Cell
	Rotation domain.Rotation
}

// PlannedCoolerSites lists a cooler site per cooling room whose exhaust
// the plan reserved, in plan order.
func PlannedCoolerSites(plan LayoutPlan) []PlannedCoolerSite {
	var out []PlannedCoolerSite
	for _, role := range coolingRoles {
		for _, r := range plan.Rooms {
			if r.Role != role {
				continue
			}
			in := r.Interior
			x, wall, first, rot := in.X+in.Width/2, in.Z+in.Height, in.Z+in.Height+1, domain.North
			if r.DoorRot == domain.North {
				wall, first, rot = in.Z-1, in.Z-2, domain.South
			}
			for _, e := range plan.Reservations {
				if e.Kind == ReserveExhaust && e.Area.X == x && (e.Area.Z == first || e.Area.Z+e.Area.Height-1 == first) {
					out = append(out, PlannedCoolerSite{Cell: domain.Cell{X: x, Z: wall}, Rotation: rot})
					break
				}
			}
		}
	}
	return out
}

// SolarDefinition is the solar generator, planned on its 4x4 plots.
const SolarDefinition = "SolarGenerator"

// RectangleCells lists r's cells row by row.
func RectangleCells(r Rectangle) []domain.Cell {
	var out []domain.Cell
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

// utilityGrid marks the cells a utility site may not take.
type utilityGrid struct {
	w, h           int32
	ok, rock       []bool // core candidate; natural rock
	used           []bool // planned rooms, spine, reservations
	bandLo, bandHi int32  // the core's cross-section rows
	cx, cz         int32  // the spine's centre
}

func newUtilityGrid(plan LayoutPlan) *utilityGrid {
	u := &utilityGrid{}
	for _, z := range plan.Zones {
		for _, r := range z.Runs {
			u.w, u.h = max(u.w, r.X+r.Length), max(u.h, r.Z+1)
		}
	}
	n := int(u.w * u.h)
	u.ok, u.rock, u.used = make([]bool, n), make([]bool, n), make([]bool, n)
	for _, z := range plan.Zones {
		var set []bool
		switch z.Kind {
		case ZoneCore:
			set = u.ok
		case ZoneMining:
			set = u.rock
		default:
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				set[r.Z*u.w+x] = true
			}
		}
	}
	for _, r := range plan.Rooms {
		u.mark(roomWalls(r))
	}
	for _, s := range plan.Spine {
		x0, x1 := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		z0, z1 := min(s.From.Z, s.To.Z), max(s.From.Z, s.To.Z)
		u.mark(Rectangle{X: x0 - SpineWidth/2, Z: z0 - SpineWidth/2, Width: x1 - x0 + SpineWidth, Height: z1 - z0 + SpineWidth})
	}
	for _, r := range plan.Reservations {
		u.mark(r.Area)
	}
	seg := plan.Spine[0]
	half := SpineWidth/2 + coreMaxDepth + 2
	u.bandLo, u.bandHi = seg.From.Z-half, seg.From.Z+half
	u.cx, u.cz = (seg.From.X+seg.To.X)/2, seg.From.Z
	return u
}

func roomWalls(r LayoutRoom) Rectangle {
	in := r.Interior
	return Rectangle{X: in.X - 1, Z: in.Z - 1, Width: in.Width + 2, Height: in.Height + 2}
}

func (u *utilityGrid) in(x, z int32) bool { return x >= 0 && z >= 0 && x < u.w && z < u.h }

func (u *utilityGrid) mark(r Rectangle) {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if u.in(x, z) {
				u.used[z*u.w+x] = true
			}
		}
	}
}

func (u *utilityGrid) reserve(plan *LayoutPlan, r LayoutReservation) {
	plan.Reservations = append(plan.Reservations, r)
	u.mark(r.Area)
}

// free reports every cell of r on unplanned core candidates, off the core
// band; rockOK lets it cross natural rock.
func (u *utilityGrid) free(r Rectangle, rockOK bool) bool {
	if r.Z+r.Height > u.bandLo && r.Z <= u.bandHi {
		return false
	}
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if !u.in(x, z) {
				return false
			}
			i := z*u.w + x
			if !u.ok[i] || u.used[i] || u.rock[i] && !rockOK {
				return false
			}
		}
	}
	return true
}

// site is the free w x h rectangle nearest the spine's centre.
func (u *utilityGrid) site(w, h int32, rockOK bool) (Rectangle, bool) {
	best, found, bestD := Rectangle{}, false, int64(-1)
	for z := int32(0); z+h <= u.h; z++ {
		for x := int32(0); x+w <= u.w; x++ {
			dx, dz := int64(x+w/2-u.cx), int64(z+h/2-u.cz)
			d := dx*dx + dz*dz
			if found && d >= bestD {
				continue
			}
			r := Rectangle{X: x, Z: z, Width: w, Height: h}
			if u.free(r, rockOK) {
				best, found, bestD = r, true, d
			}
		}
	}
	return best, found
}

// exhaust is the column behind r's back wall, away from the spine, out to
// the first open cell: one cell on an outer face, a dug shaft through
// rock. False when the back is planned or off the map.
func (u *utilityGrid) exhaust(r LayoutRoom) (Rectangle, bool) {
	in := r.Interior
	x, z, step := in.X+in.Width/2, in.Z+in.Height+1, int32(1)
	if r.DoorRot == domain.North { // room south of the spine
		z, step = in.Z-2, -1
	}
	for n := int32(1); n <= exhaustMax; n++ {
		if !u.in(x, z) || u.used[z*u.w+x] {
			return Rectangle{}, false
		}
		if !u.rock[z*u.w+x] {
			lo := z - step*(n-1)
			if step < 0 {
				lo = z
			}
			return Rectangle{X: x, Z: lo, Width: 1, Height: n}, true
		}
		z += step
	}
	return Rectangle{}, false
}
