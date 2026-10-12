package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Utility reservations. The plan holds:
//   - a battery room, a room like any other (PlannedBattery);
//   - wind turbines and solar plots as stored site nodes (NodeTurbine,
//     NodeSolar; layout_power_nodes.go), placed with the rooms by the site
//     embedding, a turbine with the wind lane the game prices;
//   - a stone enclosure around each geyser's generator;
//   - an exhaust cell (or dug shaft) behind each room that needs cooling.
// PlannedPowerSites reads them back; the generator never builds over a
// reservation.

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
	// turbineWidth is a turbine's long side, the wind path's width.
	turbineWidth   int32 = 7
	solarSide      int32 = 4
	geothermalSide int32 = 6
	// geothermalShell is the enclosure's thickness.
	geothermalShell int32 = 2
	exhaustMax      int32 = 12
)

// coolingRoles are the rooms that need a cooler, most important first;
// the tomb keeps colonist corpses frozen, the meal closet the
// dining room's meals.
var coolingRoles = []PlannedRole{PlannedFreezer, PlannedTomb, PlannedMorgue, PlannedMealCloset}

// UtilityWants is what PlanUtilities reserves: the steam geysers (each the
// geyser's 2x2 footprint) and the herd units (barn and vet room) for
// PenAnimals animals.
type UtilityWants struct {
	PenAnimals int
	Geysers    []Rectangle
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
				u.reserve(plan, LayoutReservation{Kind: ReserveExhaust, Area: RectRegion(area)})
			}
		}
	}
}

// PlanUtilities adds cooler exhausts, the geyser enclosures and the herd units
// to a spine plan. Sites that do not fit are left out. The battery room is
// grown on demand (growDemandRooms), not here; the turbines and solar plots
// are site nodes (PowerSiteNodes).
func PlanUtilities(plan LayoutPlan, want UtilityWants) LayoutPlan {
	if len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Rooms = append([]PlannedRoom(nil), plan.Rooms...)
	plan.Reservations = append([]LayoutReservation(nil), plan.Reservations...)
	plan.Zones = append([]LayoutZone(nil), plan.Zones...)
	u := newUtilityGrid(plan)
	reserveExhausts(u, &plan)
	for _, gz := range want.Geysers {
		area := geothermalArea(gz)
		// The clearance ring only keeps other sites off the hallways: a vent is
		// where it is, and core rooms were already sited off its enclosure.
		if u.freeWhere(area, true, false) {
			u.reserve(&plan, LayoutReservation{Kind: ReserveGeothermal, Area: RectRegion(area)})
		}
	}
	planHerdSites(u, &plan, want.PenAnimals, nil)
	return plan
}

// TurbinePlacement is a turbine's native centre and rotation on its 7x2 pad
// (2x7 turned): lowRow puts it on the pad's low row (z, or x turned), facing
// north (east turned); otherwise it stands on the high row facing the other way.
func TurbinePlacement(area Rectangle, lowRow bool) (domain.Cell, domain.Rotation) {
	if area.Width == 2 && area.Height == turbineWidth { // turned: the low column faces east
		if lowRow {
			return domain.Cell{X: area.X, Z: area.Z + 3}, domain.East
		}
		return domain.Cell{X: area.X + 1, Z: area.Z + 3}, domain.West
	}
	if lowRow {
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
	in := r.Region.Bounds()
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
// in a room on a crossing), wind turbines on their pads (TurbinePlacement),
// solar on its plots. Nil for any other definition or a plan with no such site.
func PlannedPowerSites(plan LayoutPlan, definition string) []PlannedPowerSite {
	var out []PlannedPowerSite
	switch definition {
	case BatteryDefinition:
		// The shelter's porch holds the batteries until the battery room is planned.
		if len(plan.roomsOf(PlannedBattery)) == 0 {
			out = append(out, porchSites(plan)...)
		}
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
		for _, s := range plan.LiveSites(NodeTurbine) {
			i := slices.IndexFunc(turbineTurns, func(t turbineTurn) bool { return t.rot == s.Rotation })
			if i < 0 {
				continue
			}
			area := s.Footprint.Bounds()
			c, rot := TurbinePlacement(area, turbineTurns[i].lowRow)
			out = append(out, PlannedPowerSite{Cell: c, Rotation: rot, Area: area})
		}
	case SolarDefinition:
		for _, s := range plan.LiveSites(NodeSolar) {
			a := s.Footprint.Bounds()
			out = append(out, PlannedPowerSite{Cell: domain.Cell{X: a.X + 1, Z: a.Z + 1}, Rotation: domain.North, Area: a})
		}
	case GeothermalDefinition:
		// The 6x6 generator inside the shell, on the geyser's position: a
		// 6-wide footprint spans its centre -2..+3.
		for _, r := range plan.Reservations {
			if r.Kind == ReserveGeothermal {
				gen := pad(r.Area.Bounds(), -geothermalShell)
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
	in := room.Region.Bounds()
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
		if e.Kind == ReserveExhaust && (e.Area.Bounds().Width == 1 || e.Area.Bounds().Height == 1) && e.Area.Contains(first) {
			return PlannedCoolerSite{Cell: wall, Rotation: rot}, e.Area.Bounds(), true
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
		if r.attached() {
			u.mark(r.attachedGround())
		}
	}
	for _, s := range plan.Hallways() {
		x0, x1 := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		z0, z1 := min(s.From.Z, s.To.Z), max(s.From.Z, s.To.Z)
		u.mark(Rectangle{X: x0 - SpineWidth/2, Z: z0 - SpineWidth/2, Width: x1 - x0 + SpineWidth, Height: z1 - z0 + SpineWidth})
	}
	for _, r := range plan.Reserved() {
		u.markRegion(r.Area)
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
	if r.Role == PlannedSparringRing {
		return ringBlock(r)
	}
	if r.Unfenced() {
		// No walls: a range blocks its rangeBlock, any other room its interior.
		if r.Role == PlannedTrainingRange {
			return rangeBlock(r)
		}
		return r.Region.Bounds()
	}
	in := r.Region.Bounds()
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

func (u *utilityGrid) markRegion(g Region) {
	for _, c := range g.Cells() {
		if u.in(c.X, c.Z) {
			u.used[c.Z*u.w+c.X] = true
		}
	}
}

func (u *utilityGrid) reserve(plan *LayoutPlan, r LayoutReservation) {
	plan.Reservations = append(plan.Reservations, r)
	u.markRegion(r.Area)
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
	off, front, back := int32(0), windPathAhead-1, windPathBehind-1
	if rot != domain.North && rot != domain.East {
		off, front, back = -1, windPathBehind-1, windPathAhead-1
	}
	half := windPathWidth / 2
	var a, b Rectangle // X, Z, Width, Height as min/extent
	if rot == domain.East || rot == domain.West {
		a = Rectangle{X: center.X + windPathStart + off, Z: center.Z - half, Width: front + 1, Height: windPathWidth}
		b = Rectangle{X: center.X - 1 - back + off, Z: center.Z - half, Width: back + 1, Height: windPathWidth}
	} else {
		a = Rectangle{X: center.X - half, Z: center.Z + windPathStart + off, Width: windPathWidth, Height: front + 1}
		b = Rectangle{X: center.X - half, Z: center.Z - 1 - back + off, Width: windPathWidth, Height: back + 1}
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
