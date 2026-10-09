package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Utility reservations. After the core is laid out the plan
// holds:
//   - a battery room off the spine, a room like any other (PlannedBattery);
//   - wind turbines in facing north/south pairs whose catch zones overlap
//     in one shared lane, every zone cell a field and none roofed, walled,
//     rock or core;
//   - solar on its own clear 4x4 plots;
//   - a stone enclosure around each geyser's generator;
//   - an exhaust cell (or dug shaft) behind each room that needs cooling.
// Turbine, solar and geothermal sites stay out of the core's cross-section
// band so the spine can keep growing along it, and the generator never builds over
// a reservation.

// PlannedBattery is the battery room: 5 wide, a 1-cell walkway from the
// door down the middle, batteries (1x2) along both sides with a stone
// block between each, roofed, stone door.
const PlannedBattery PlannedRole = "battery"

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
// the tomb keeps colonist corpses frozen, the meal closet the
// dining room's meals.
var coolingRoles = []PlannedRole{PlannedFreezer, PlannedTomb, PlannedMorgue, PlannedMealCloset}

// UtilityWants is what PlanUtilities reserves: turbine pairs, solar plots,
// the steam geysers (each the geyser's 2x2 footprint) and the herd units (barn
// and vet room) for PenAnimals animals.
type UtilityWants struct {
	TurbinePairs, Solar int
	PenAnimals          int
	Geysers             []Rectangle
	// ThickRoof is the surveyed cells under overhead mountain; non-nil lets
	// turbines and solar plots be sited on natural rock, dug and unroofed
	// first, except where a cell is under thick roof (it cannot be
	// removed). Nil keeps those sites off rock.
	ThickRoof map[domain.Cell]bool
	// scorer sites the battery room with the initial siting's costing; nil
	// takes the nearest slot.
	scorer *planScorer
}

// ThickRoofCells is the survey's cells under thick mountain roof, for
// UtilityWants.ThickRoof; never nil.
func ThickRoofCells(s MapSurvey) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.ThickRoof {
			out[c.Cell] = true
		}
	}
	return out
}

// reserveExhausts gives each cooling room that has none a cooler exhaust.
// Rooms already holding one are left alone, so it is safe to run again after
// a cooled room is grown (growDemandRooms).
func reserveExhausts(u *utilityGrid, plan *LayoutPlan) {
	for _, role := range coolingRoles {
		for _, r := range plan.AllRooms() {
			if r.Role != role {
				continue
			}
			if _, _, has := plan.CoolerExhaust(r); has {
				continue
			}
			if area, ok := u.exhaust(r); ok {
				u.reserve(plan, LayoutReservation{Kind: ReserveExhaust, Area: area})
			}
		}
	}
}

// PlanUtilities adds cooler exhausts and the wanted generator sites to plan.
// Sites that do not fit are left out. The battery room is grown on demand
// (growDemandRooms), not here.
func PlanUtilities(plan LayoutPlan, want UtilityWants) LayoutPlan {
	if len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Rooms = append([]PlannedRoom(nil), plan.Rooms...)
	plan.Reservations = append([]LayoutReservation(nil), plan.Reservations...)
	plan.Zones = append([]LayoutZone(nil), plan.Zones...)
	u := newUtilityGrid(plan)
	u.thick = want.ThickRoof
	skyRock := want.ThickRoof != nil
	reserveExhausts(u, &plan)
	for _, gz := range want.Geysers {
		area := geothermalArea(gz)
		// The clearance ring only keeps other sites off the hallways: a vent is
		// where it is, and core rooms were already sited off its enclosure.
		if u.freeWhere(area, true, false) {
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
		// A pair stands north-south or, turned, east-west: whichever lies
		// nearer the core, where its lanes double as fields.
		fx, fz := u.cx, u.cz
		site, cost, ok := u.siteCost(turbineWidth, turbinePairSpan, skyRock, false, fx, fz, 0)
		turned := false
		if t, tcost, tok := u.siteCost(turbinePairSpan, turbineWidth, skyRock, false, fx, fz, 0); tok && (!ok || tcost < cost) {
			site, turned, ok = t, true, true
		}
		if !ok {
			break
		}
		pair++
		for _, r := range turbinePair(site, pair, turned) {
			u.reserve(&plan, r)
		}
		var runs []RowRun
		for z := site.Z; z < site.Z+site.Height; z++ {
			// Cells already planned as farmland are not planned twice.
			var start int32 = -1
			flush := func(end int32) {
				if start >= 0 {
					runs = append(runs, RowRun{Z: z, X: start, Length: end - start})
					start = -1
				}
			}
			for x := site.X; x < site.X+site.Width; x++ {
				dx, dz := x-site.X, z-site.Z
				if turned {
					dx, dz = dz, dx
				}
				foot := dz == 6 || dz == 7 || dz == 18 || dz == 19 // turbine footprints
				if foot || u.field[z*u.w+x] {
					flush(x)
					continue
				}
				if start < 0 {
					start = x
				}
			}
			flush(site.X + site.Width)
		}
		plan.Zones = append(plan.Zones, LayoutZone{Kind: ZoneField, Runs: runs})
		for _, run := range runs {
			for x := run.X; x < run.X+run.Length; x++ {
				u.field[run.Z*u.w+x] = true
			}
		}
	}
	for i := 0; i < want.Solar; i++ {
		site, ok := u.site(solarSide, solarSide, skyRock, false, u.cx, u.cz)
		if !ok {
			break
		}
		u.reserve(&plan, LayoutReservation{Kind: ReserveSolar, Area: site})
	}
	planHerdSites(u, &plan, want.PenAnimals, nil)
	return plan
}

// turbinePair lays a facing pair in the 7x26 site: the north-facing
// turbine at rows 6-7, the south-facing one at rows 18-19, and three lanes
// (its back zone, the shared 10-row lane, the other's back zone).
func turbinePair(site Rectangle, pair int32, turned bool) []LayoutReservation {
	row := func(dz, h int32, kind ReservationKind) LayoutReservation {
		a := Rectangle{X: site.X, Z: site.Z + dz, Width: turbineWidth, Height: h}
		if turned {
			a = Rectangle{X: site.X + dz, Z: site.Z, Width: h, Height: turbineWidth}
		}
		return LayoutReservation{Kind: kind, Area: a, Pair: pair}
	}
	return []LayoutReservation{
		row(6, 2, ReserveTurbine), row(18, 2, ReserveTurbine),
		row(0, 6, ReserveTurbineLane), row(8, 10, ReserveTurbineLane), row(20, 6, ReserveTurbineLane),
	}
}

// TurbinePlacement is a turbine reservation's native centre and rotation:
// the pair's southern turbine faces north, its northern one south.
func TurbinePlacement(area Rectangle, pairSouth bool) (domain.Cell, domain.Rotation) {
	if area.Width == 2 && area.Height == turbineWidth { // a turned pair: the western one faces east
		if pairSouth {
			return domain.Cell{X: area.X, Z: area.Z + 3}, domain.East
		}
		return domain.Cell{X: area.X + 1, Z: area.Z + 3}, domain.West
	}
	if pairSouth {
		return domain.Cell{X: area.X + 3, Z: area.Z}, domain.North
	}
	return domain.Cell{X: area.X + 3, Z: area.Z + 1}, domain.South
}

// BatterySlots are a battery room's battery footprints (1x2 across the
// walkway on each side), nearest the door first; the rows between them
// hold the stone blocks. A room on a crossing (east or west door) is the
// same room transposed: 1x2 slots along z, rows stepping along x.
func BatterySlots(r PlannedRoom) []Rectangle {
	if r.DoorRot == domain.East || r.DoorRot == domain.West {
		out := BatterySlots(transposeRoom(r))
		for i, s := range out {
			out[i] = Rectangle{X: s.Z, Z: s.X, Width: s.Height, Height: s.Width}
		}
		return out
	}
	in := r.Interior
	var out []Rectangle
	for _, i := range AisleRows(in.Height, 2) {
		z := in.Z + i
		if r.DoorRot == domain.North { // door on the north wall
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

// PlannedPowerSites lists the plan's sites for definition:
// batteries in the battery room's slots (1x2 turned east, or facing north
// in a room on a crossing), wind turbines on
// their pair reservations (the southern one facing north), solar on its
// plots. Nil for any other definition or a plan with no such site.
func PlannedPowerSites(plan LayoutPlan, definition string) []PlannedPowerSite {
	var out []PlannedPowerSite
	switch definition {
	case BatteryDefinition:
		for _, r := range plan.AllRooms() {
			if r.Role != PlannedBattery {
				continue
			}
			slots := BatterySlots(r)
			for i, s := range slots {
				// A 1x2 battery turned east spans x..x+1; facing north z..z+1.
				rot := domain.East
				if s.Height == 2 {
					rot = domain.North
				}
				site := PlannedPowerSite{Cell: domain.Cell{X: s.X, Z: s.Z}, Rotation: rot, Area: s}
				if i >= 2 {
					prev := slots[i-2]
					if rot == domain.North {
						site.Block = Rectangle{X: (s.X + prev.X) / 2, Z: s.Z, Width: 1, Height: s.Height}
					} else {
						site.Block = Rectangle{X: s.X, Z: (s.Z + prev.Z) / 2, Width: s.Width, Height: 1}
					}
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
				if o.Kind == ReserveTurbine && o.Pair == r.Pair && (o.Area.Z < r.Area.Z || o.Area.X < r.Area.X) {
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

// PlannedCoolerSite is a cooled room's planned cooler: the cell in
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
		for _, r := range plan.AllRooms() {
			if r.Role != role {
				continue
			}
			if site, _, ok := plan.CoolerExhaust(r); ok {
				out = append(out, site)
			}
		}
	}
	return out
}

// backWall is room's back wall cell (the middle of the wall facing away
// from its hallway), the step pointing out through it, and that direction.
func backWall(room PlannedRoom) (domain.Cell, domain.Cell, domain.Rotation) {
	in := room.Interior
	switch room.DoorRot {
	case domain.North: // room south of an east-west hallway
		return domain.Cell{X: in.X + in.Width/2, Z: in.Z - 1}, domain.Cell{Z: -1}, domain.South
	case domain.East: // room west of a crossing
		return domain.Cell{X: in.X - 1, Z: in.Z + in.Height/2}, domain.Cell{X: -1}, domain.West
	case domain.West:
		return domain.Cell{X: in.X + in.Width, Z: in.Z + in.Height/2}, domain.Cell{X: 1}, domain.East
	}
	return domain.Cell{X: in.X + in.Width/2, Z: in.Z + in.Height}, domain.Cell{Z: 1}, domain.North
}

// CoolerExhaust is the cooler site in room's back wall and the exhaust the
// plan reserved behind it; false when the plan reserved none.
func (p LayoutPlan) CoolerExhaust(room PlannedRoom) (PlannedCoolerSite, Rectangle, bool) {
	wall, step, rot := backWall(room)
	first := domain.Cell{X: wall.X + step.X, Z: wall.Z + step.Z}
	for _, e := range p.Reservations {
		if e.Kind == ReserveExhaust && (e.Area.Width == 1 || e.Area.Height == 1) && rectsOverlap(e.Area, Rectangle{X: first.X, Z: first.Z, Width: 1, Height: 1}) {
			return PlannedCoolerSite{Cell: wall, Rotation: rot}, e.Area, true
		}
	}
	return PlannedCoolerSite{}, Rectangle{}, false
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
	w, h     int32
	ok, rock []bool // core candidate; natural rock
	field    []bool // planned farmland: sites avoid it
	thick    map[domain.Cell]bool
	keepOut  []bool // the core ring and the clearance the outer ring needs
	used     []bool // planned rooms, spine, reservations
	clear    []bool // the hallways and a clearance ring: no site stands here
	cx, cz   int32  // the plan core (LayoutPlan.Core)
}

func newUtilityGrid(plan LayoutPlan) *utilityGrid {
	u := &utilityGrid{}
	for _, z := range plan.Zones {
		for _, r := range z.Runs {
			u.w, u.h = max(u.w, r.X+r.Length), max(u.h, r.Z+1)
		}
	}
	n := int(u.w * u.h)
	u.ok, u.rock, u.used, u.field = make([]bool, n), make([]bool, n), make([]bool, n), make([]bool, n)
	for _, z := range plan.Zones {
		var set []bool
		switch z.Kind {
		case ZoneCore:
			set = u.ok
		case ZoneMining:
			set = u.rock
		case ZoneField:
			set = u.field
		default:
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				set[r.Z*u.w+x] = true
			}
		}
	}
	for _, r := range plan.AllRooms() {
		u.mark(roomWalls(r))
	}
	for _, s := range plan.Hallways() {
		x0, x1 := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		z0, z1 := min(s.From.Z, s.To.Z), max(s.From.Z, s.To.Z)
		u.mark(Rectangle{X: x0 - SpineWidth/2, Z: z0 - SpineWidth/2, Width: x1 - x0 + SpineWidth, Height: z1 - z0 + SpineWidth})
	}
	for _, r := range plan.Reservations {
		u.mark(r.Area)
	}
	u.keepOut = outerKeepOut(plan, u.w, u.h)
	u.clear = make([]bool, n)
	ring := coreHalf
	for _, r := range spineRects(plan.Hallways()) {
		for z := max(r.Z-ring, 0); z < min(r.Z+r.Height+ring, u.h); z++ {
			for x := max(r.X-ring, 0); x < min(r.X+r.Width+ring, u.w); x++ {
				u.clear[z*u.w+x] = true
			}
		}
	}
	if c, ok := plan.Core(); ok {
		u.cx, u.cz = c.X, c.Z
	}
	return u
}

func roomWalls(r PlannedRoom) Rectangle {
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

// siteInset is how far from the map edge a core-side site starts: the ring walls
// a site in from the edge margin inward, so one nearer the edge would stand
// on the ring itself.
const siteInset = LayoutEdgeMargin + perimeterThick + 1

// free reports every cell of r on unplanned core candidates, off the core
// hallway clearance; rockOK lets it cross natural rock.
func (u *utilityGrid) free(r Rectangle, rockOK bool) bool { return u.freeWhere(r, rockOK, true) }

// freeWhere is free, optionally letting r stand in the hallway clearance ring.
func (u *utilityGrid) freeWhere(r Rectangle, rockOK, clearBlocks bool) bool {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if !u.in(x, z) {
				return false
			}
			i := z*u.w + x
			if !u.ok[i] || u.used[i] || clearBlocks && u.clear[i] || u.rock[i] && !rockOK {
				return false
			}
		}
	}
	return true
}

// siteFieldReach is how many cells farther a site lying wholly on planned
// farmland is worth moving to avoid it: generators and plots are pushed off
// rich soil only within the field reach, never across the map.
var siteFieldReach = float64(perimeterFieldReach)

// fieldCells counts r's cells planned as farmland.
func (u *utilityGrid) fieldCells(r Rectangle) int {
	n := 0
	for z := max(r.Z, 0); z < min(r.Z+r.Height, u.h); z++ {
		for x := max(r.X, 0); x < min(r.X+r.Width, u.w); x++ {
			if u.field[z*u.w+x] {
				n++
			}
		}
	}
	return n
}

func (u *utilityGrid) outside(r Rectangle) bool {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if u.in(x, z) && u.keepOut[z*u.w+x] {
				return false
			}
		}
	}
	return true
}

// site is the free w x h rectangle nearest (cx, cz), a site wholly on
// planned farmland counting siteFieldReach cells farther. beyondRing keeps
// it off the core ring's keep-out, for sites the outer ring encloses.
func (u *utilityGrid) site(w, h int32, rockOK, beyondRing bool, cx, cz int32) (Rectangle, bool) {
	r, _, ok := u.siteCost(w, h, rockOK, beyondRing, cx, cz, siteFieldReach)
	return r, ok
}

// skyRockPenalty is how many cells farther a site wholly on rock is worth
// moving to reach open ground: digging and unroofing cost far more.
const skyRockPenalty = 30.0

// siteCost is site with an explicit farmland penalty (0 for sites that
// belong on farmland) and the winning cost. A rockOK site is a sky site:
// it crosses rock but never a thick-roof cell, and rock costs extra.
func (u *utilityGrid) siteCost(w, h int32, rockOK, beyondRing bool, cx, cz int32, fieldReach float64) (Rectangle, float64, bool) {
	best, found, bestCost := Rectangle{}, false, 0.0
	for z := int32(0); z+h <= u.h; z++ {
		for x := int32(0); x+w <= u.w; x++ {
			dx, dz := float64(x+w/2-cx), float64(z+h/2-cz)
			cost := math.Sqrt(dx*dx + dz*dz)
			if found && cost >= bestCost {
				continue
			}
			r := Rectangle{X: x, Z: z, Width: w, Height: h}
			cost += fieldReach * float64(u.fieldCells(r)) / float64(w*h)
			if found && cost >= bestCost {
				continue
			}
			rock := 0
			if rockOK {
				rock = u.skyRock(r)
			}
			if rock < 0 {
				continue
			}
			cost += skyRockPenalty * float64(rock) / float64(w*h)
			if found && cost >= bestCost {
				continue
			}
			if u.free(r, rockOK) && (beyondRing && u.outside(r) || !beyondRing && u.inset(r)) {
				best, found, bestCost = r, true, cost
			}
		}
	}
	return best, bestCost, found
}

// skyRock counts r's rock cells, or -1 when any cell is under thick roof.
func (u *utilityGrid) skyRock(r Rectangle) int {
	n := 0
	for z := max(r.Z, 0); z < min(r.Z+r.Height, u.h); z++ {
		for x := max(r.X, 0); x < min(r.X+r.Width, u.w); x++ {
			if u.thick[domain.Cell{X: x, Z: z}] {
				return -1
			}
			if u.rock[z*u.w+x] {
				n++
			}
		}
	}
	return n
}

// exhaust is the column behind r's back wall, away from the spine, out to
// the first open cell: one cell on an outer face, a dug shaft through
// rock. False when the back is planned or off the map.
func (u *utilityGrid) exhaust(r PlannedRoom) (Rectangle, bool) {
	wall, step, _ := backWall(r)
	first := domain.Cell{X: wall.X + step.X, Z: wall.Z + step.Z}
	c := first
	for n := int32(1); n <= exhaustMax; n++ {
		if !u.in(c.X, c.Z) || u.used[c.Z*u.w+c.X] {
			return Rectangle{}, false
		}
		if !u.rock[c.Z*u.w+c.X] {
			return Rectangle{X: min(first.X, c.X), Z: min(first.Z, c.Z), Width: abs32(c.X-first.X) + 1, Height: abs32(c.Z-first.Z) + 1}, true
		}
		c = domain.Cell{X: c.X + step.X, Z: c.Z + step.Z}
	}
	return Rectangle{}, false
}

// TurbineCatchZone is the cells the turbine standing on area must keep
// clear: its own wind path, not the pair's whole lane area. Nil for a site
// that is not a turbine (a solar plot).
func TurbineCatchZone(plan LayoutPlan, area Rectangle) []domain.Cell {
	for _, site := range PlannedPowerSites(plan, WindTurbineDefinition) {
		if site.Area == area {
			return TurbineWindCells(site.Cell, site.Rotation)
		}
	}
	return nil
}

// TurbineWindCells is CompPowerPlantWind's wind path for a turbine centred on
// centre (WindTurbineUtility.CalculateWindCells, read with ilspycmd): 7 wide,
// 10 rows in front starting two out and 6 behind. A roof of any kind, or a
// wind-blocking thing, on a cell cuts the output by 20 percent; the
// turbine's size does not enter.
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

// inset reports r clear of siteInset around the map edge.
func (u *utilityGrid) inset(r Rectangle) bool {
	return r.X >= siteInset && r.Z >= siteInset && r.X+r.Width <= u.w-siteInset && r.Z+r.Height <= u.h-siteInset
}
