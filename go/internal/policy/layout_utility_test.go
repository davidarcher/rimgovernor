package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// utilityTestZones is coreTestZones on a map wide enough for utility sites
// beyond the core ring's keep-out.
func utilityTestZones() []LayoutZone {
	return Zone(zoningSurvey(220, func(x, z int32) SurveyCell {
		if x >= 180 {
			return SurveyCell{Rock: true}
		}
		return SurveyCell{Walkable: true, Fertility: 1}
	}))
}

func TestPlannedPowerSites(t *testing.T) {
	p := PlanUtilities(withRooms(corePlan(utilityTestZones(), 3, BuildTierCamp), PlannedBattery), UtilityWants{TurbinePairs: 1, Solar: 1})
	batteries := PlannedPowerSites(p, BatteryDefinition)
	if len(batteries) != 8 || batteries[0].Block != (Rectangle{}) || batteries[1].Block != (Rectangle{}) {
		t.Fatal(batteries)
	}
	for _, b := range batteries[2:] {
		if b.Rotation != domain.East || b.Block.Height != 1 || b.Block.Width != 2 || b.Block.X != b.Area.X {
			t.Fatal(b)
		}
		if d := b.Block.Z - b.Area.Z; d != 1 && d != -1 {
			t.Fatal("block not beside its battery", b)
		}
	}
	turbines := PlannedPowerSites(p, WindTurbineDefinition)
	if len(turbines) != 2 || turbines[0].Rotation == turbines[1].Rotation {
		t.Fatal(turbines)
	}
	for _, s := range turbines {
		for _, c := range TurbineWindCells(s.Cell, s.Rotation) {
			for _, o := range turbines {
				if inRect(o.Area, c) {
					t.Fatal("catch zone crosses a turbine", s, c)
				}
			}
		}
	}
	if solar := PlannedPowerSites(p, SolarDefinition); len(solar) != 1 {
		t.Fatal(solar)
	}
}

// TestBatteryRoomOnCrossing (#1265): with bedroom wings filling the main
// hallway the battery room still gets a site, on a crossing when needed,
// its slots nearest the door first and every block beside its battery.
func TestBatteryRoomOnCrossing(t *testing.T) {
	p := PlanUtilities(withRooms(corePlan(coreTestZones(), 12, BuildTierCamp), PlannedBattery), UtilityWants{})
	batteries := PlannedPowerSites(p, BatteryDefinition)
	if len(batteries) != 8 {
		t.Fatal(batteries)
	}
	var room PlannedRoom
	for _, r := range p.AllRooms() {
		if r.Role == PlannedBattery {
			room = r
		}
	}
	along := func(c domain.Cell) int32 { return abs32(c.Z - room.Door.Z) }
	if room.DoorRot == domain.East || room.DoorRot == domain.West {
		along = func(c domain.Cell) int32 { return abs32(c.X - room.Door.X) }
	}
	if along(batteries[0].Cell) > along(batteries[7].Cell) {
		t.Fatal("first row farther from the door", room, batteries)
	}
	for _, b := range batteries {
		for _, c := range RectangleCells(b.Area) {
			if !inRect(room.Interior, c) {
				t.Fatal("battery outside the room", room, b)
			}
		}
		if b.Block != (Rectangle{}) && !inRect(room.Interior, domain.Cell{X: b.Block.X, Z: b.Block.Z}) {
			t.Fatal("block outside the room", room, b)
		}
	}
}

// TestBatterySlotsCrossing: an east-door room's slots are the north-door
// room's transposed, 1x2 along z, and plan north-facing batteries.
func TestBatterySlotsCrossing(t *testing.T) {
	room := PlannedRoom{Role: PlannedBattery, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 17, Z: 22}, DoorRot: domain.East}
	slots := BatterySlots(room)
	if len(slots) != 8 || slots[0] != (Rectangle{X: 16, Z: 20, Width: 1, Height: 2}) || slots[1] != (Rectangle{X: 16, Z: 23, Width: 1, Height: 2}) || slots[2].X != 14 {
		t.Fatal(slots)
	}
	sites := PlannedPowerSites(LayoutPlan{Rooms: []PlannedRoom{room}}, BatteryDefinition)
	if sites[2].Rotation != domain.North || sites[2].Block != (Rectangle{X: 15, Z: 20, Width: 1, Height: 2}) {
		t.Fatal(sites[2])
	}
}

// TestPlannedCoolerSites: each cooled room's cooler stands in its back wall
// with the cold side in the room and the hot side on the exhaust (#791),
// on either hallway (#952): the freezer and the soil tomb both get one.
func TestPlannedCoolerSites(t *testing.T) {
	p := PlanUtilities(growPlan(corePlan(coreTestZones(), 0, BuildTierCamp), 0, 1, BuildTierCamp), UtilityWants{})
	sites := PlannedCoolerSites(p)
	if len(sites) != 2 {
		t.Fatal(sites)
	}
	for _, s := range sites {
		c := RefrigerationCooler{Position: s.Cell, Rotation: s.Rotation}
		var room *PlannedRoom
		for i, r := range p.Rooms {
			if inRect(r.Interior, c.Cold()) {
				room = &p.Rooms[i]
			}
		}
		hot := false
		for _, r := range p.Reservations {
			hot = hot || r.Kind == ReserveExhaust && inRect(r.Area, c.Hot())
		}
		if room == nil || !hot || inRect(roomWalls(*room), c.Hot()) || !inRect(roomWalls(*room), c.Position) {
			t.Fatal(s, room)
		}
	}
}

// TestRefrigerationPrefersPlannedCooler: a planned site with an open hot
// side beats the lowest-sorted vented wall; a closed one falls back.
func TestRefrigerationPrefersPlannedCooler(t *testing.T) {
	room := Room{ID: "r", Enclosed: domain.Known(true)}
	var cells []SiteCell
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			c := domain.Cell{X: x, Z: z}
			in := x >= 1 && x <= 3 && z >= 1 && z <= 3
			wall := !in && (x == 0 || x == 4 || z == 0 || z == 4)
			if in {
				room.Cells = append(room.Cells, c)
			}
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(!wall), Occupied: domain.Known(wall), Indoors: domain.Known(in), Roofed: domain.Known(in)})
		}
	}
	for x := int32(-1); x <= 5; x++ {
		for _, z := range []int32{-1, 5} {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
		}
	}
	for z := int32(0); z < 5; z++ {
		for _, x := range []int32{-1, 5} {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
		}
	}
	review := RefrigerationReview{Active: true, Rooms: []string{"r"}}
	obs := RefrigerationObservation{Rooms: []Room{room}, Cells: cells, CoolerAvailable: domain.Known(true), Planned: []PlannedCoolerSite{{Cell: domain.Cell{X: 2, Z: 4}, Rotation: domain.North}}}
	got, err := SelectRefrigerationMethod(review, domain.Known(obs), FoodStoragePolicy{}, false)
	if err != nil || got.Cell != (domain.Cell{X: 2, Z: 4}) || got.Rotation != domain.North {
		t.Fatal(got, err)
	}
	for i := range obs.Cells {
		if obs.Cells[i].Cell == (domain.Cell{X: 2, Z: 5}) {
			obs.Cells[i].Walkable = domain.Known(false) // undug shaft
		}
	}
	got, err = SelectRefrigerationMethod(review, domain.Known(obs), FoodStoragePolicy{}, false)
	if err != nil || got.Method != RefrigerationBuild || got.Cell == (domain.Cell{X: 2, Z: 4}) {
		t.Fatal(got, err)
	}
	for i := range obs.Cells {
		switch obs.Cells[i].Cell {
		case domain.Cell{X: 2, Z: 5}:
			obs.Cells[i].Walkable = domain.Known(true) // shaft dug
		case domain.Cell{X: 2, Z: 4}:
			obs.Cells[i].NaturalRock = domain.Known(true) // back wall still rock (#836)
		}
	}
	got, err = SelectRefrigerationMethod(review, domain.Known(obs), FoodStoragePolicy{}, false)
	if err != nil || got.Method != RefrigerationBuild || got.Cell == (domain.Cell{X: 2, Z: 4}) {
		t.Fatal(got, err)
	}
}

func inRect(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}

// TestTurbineWindCells pins the mirror to the 1.6 CalculateWindCells rects.
func TestTurbineWindCells(t *testing.T) {
	for _, tc := range []struct {
		rot         domain.Rotation
		zLo, zHi, n int32
	}{{domain.North, 4, 21, 112}, {domain.South, -1, 16, 112}} {
		cells := TurbineWindCells(domain.Cell{X: 10, Z: 10}, tc.rot)
		lo, hi := int32(99), int32(-99)
		for _, c := range cells {
			lo, hi = min(lo, c.Z), max(hi, c.Z)
			if c.X < 7 || c.X > 13 {
				t.Fatal("x", c)
			}
		}
		if int32(len(cells)) != tc.n || lo != tc.zLo || hi != tc.zHi {
			t.Fatal(tc.rot, len(cells), lo, hi)
		}
	}
	north := TurbineWindCells(domain.Cell{X: 10, Z: 10}, domain.North)
	for _, c := range north {
		if c.Z == 10 || c.Z == 11 {
			t.Fatal("footprint in its own zone", c)
		}
	}
}

func TestPlanUtilities(t *testing.T) {
	zones := utilityTestZones()
	core := withRooms(growPlan(corePlan(zones, 0, BuildTierCamp), 0, 1, BuildTierCamp), PlannedBattery, PlannedMorgue)
	p := PlanUtilities(core, UtilityWants{TurbinePairs: 2, Solar: 2, Geysers: []Rectangle{{X: 20, Z: 190, Width: 2, Height: 2}}})
	if !p.Valid() {
		t.Fatal("invalid")
	}
	g := newCoreGrid(zones, nil)
	count := map[ReservationKind]int{}
	var turbines, lanes []LayoutReservation
	for _, r := range p.Reservations {
		count[r.Kind]++
		switch r.Kind {
		case ReserveTurbine:
			turbines = append(turbines, r)
		case ReserveTurbineLane:
			lanes = append(lanes, r)
		}
	}
	if count[ReserveTurbine] != 4 || count[ReserveTurbineLane] != 6 || count[ReserveSolar] != 2 || count[ReserveGeothermal] != 1 || count[ReserveExhaust] != 3 {
		t.Fatal(count)
	}
	// Battery room: 5 wide off the spine, door on the walkway, no lane.
	var battery *PlannedRoom
	for i := range p.Rooms {
		if p.Rooms[i].Role == PlannedBattery {
			battery = &p.Rooms[i]
		}
	}
	if battery == nil || battery.Interior.Width != 5 || battery.Door.X != battery.Interior.X+2 {
		t.Fatal("battery", battery)
	}
	if n := len(BatterySlots(*battery)); n != 8 {
		t.Fatal("slots", n)
	}
	checkCore(t, LayoutPlan{Spine: p.Spine, Rooms: p.Rooms[:len(core.Rooms)], Zones: zones}, 0)

	planned := func(c domain.Cell) bool {
		for _, r := range p.Rooms {
			if inRect(roomWalls(r), c) {
				return true
			}
		}
		for _, s := range p.Spine {
			if c.X >= s.From.X && c.X <= s.To.X && c.Z >= s.From.Z-1 && c.Z <= s.From.Z+1 {
				return true
			}
		}
		return false
	}
	for _, tb := range turbines {
		south := false
		for _, o := range turbines {
			if o.Pair == tb.Pair && (o.Area.Z > tb.Area.Z || o.Area.X > tb.Area.X) {
				south = true
			}
		}
		center, rot := TurbinePlacement(tb.Area, south)
		inLane := 0
		for _, c := range TurbineWindCells(center, rot) {
			if g.rock[c] || !g.core[c] || planned(c) {
				t.Fatal("blocked wind cell", c)
			}
			for _, o := range turbines {
				if inRect(o.Area, c) {
					t.Fatal("turbine in a zone", o, c)
				}
			}
			for _, l := range lanes {
				if l.Pair == tb.Pair && inRect(l.Area, c) {
					inLane++
					break
				}
			}
			field := false
			for _, z := range p.Zones {
				for _, run := range z.Runs {
					field = field || run.Z == c.Z && c.X >= run.X && c.X < run.X+run.Length
				}
			}
			if !field {
				t.Fatal("wind cell not a field", c)
			}
		}
		if inLane != 112 {
			t.Fatal("wind cells outside lanes", inLane)
		}
	}
	// The generator never builds over a reservation.
	grown := growPlan(p, 20, 1, BuildTierCamp)
	for _, r := range grown.Rooms {
		for _, res := range p.Reservations {
			w := roomWalls(r)
			if w.X < res.Area.X+res.Area.Width && res.Area.X < w.X+w.Width && w.Z < res.Area.Z+res.Area.Height && res.Area.Z < w.Z+w.Height {
				t.Fatal("room over reservation", r, res)
			}
		}
	}
}

func TestPlanUtilitiesPen(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	core := corePlan(utilityTestZones(), 3, BuildTierCamp)
	p := PlanUtilities(core, UtilityWants{Solar: 1, PenAnimals: 30})
	var pen *LayoutReservation
	for i, r := range p.Reservations {
		if r.Kind == ReservePen {
			pen = &p.Reservations[i]
		}
	}
	if pen == nil {
		t.Fatal("no pen")
	}
	if w, h := penSide(30); w*h < 30*penCellsPerAnimal || h > w || w-h > 1 {
		t.Fatal("pen not near-square", w, h)
	}
	if a := pen.Area; a.Width*a.Height < 30*penCellsPerAnimal {
		t.Fatal("pen too small", a)
	}
	for _, r := range p.Reservations {
		if r.Kind != ReservePen && rectsOverlap(r.Area, pen.Area) {
			t.Fatal("pen overlaps", r)
		}
	}
	for _, r := range p.AllRooms() {
		if rectsOverlap(roomWalls(r), pen.Area) {
			t.Fatal("pen overlaps room", r.Role)
		}
	}
	u := newUtilityGrid(core)
	if !u.free(pen.Area, true) {
		t.Fatal("pen in the hallway clearance", pen.Area)
	}
	// The site search follows the centre it is given.
	w, h := penSide(30)
	near, _ := u.site(w, h, false, false, 10, 10)
	far, _ := u.site(w, h, false, false, 70, 110)
	if near.X >= far.X && near.Z >= far.Z || near == far {
		t.Fatal("site ignores its centre", near, far)
	}
	found := false
	for _, l := range p.Overlay(Bounds{Width: 400, Height: 400}).Layers {
		found = found || l.Label == "animal pen"
	}
	if !found {
		t.Fatal("overlay lacks the pen")
	}
	for _, r := range PlanUtilities(core, UtilityWants{PenAnimals: 2000}).Reservations {
		if r.Kind == ReservePen {
			t.Fatal("pen reserved with no room")
		}
	}
}

// TestTurnedTurbinePair: an east-west pair faces its turbines across the
// shared lane and every wind cell lies in the pair's lanes.
func TestTurnedTurbinePair(t *testing.T) {
	site := Rectangle{X: 40, Z: 50, Width: turbinePairSpan, Height: turbineWidth}
	var turbines, lanes []LayoutReservation
	for _, r := range turbinePair(site, 1, true) {
		if r.Kind == ReserveTurbine {
			turbines = append(turbines, r)
		} else {
			lanes = append(lanes, r)
		}
	}
	if len(turbines) != 2 || turbines[0].Area.Width != 2 || turbines[0].Area.Height != turbineWidth {
		t.Fatal(turbines)
	}
	rots := map[domain.Rotation]bool{}
	for i, tb := range turbines {
		centre, rot := TurbinePlacement(tb.Area, i == 0)
		rots[rot] = true
		for _, c := range TurbineWindCells(centre, rot) {
			in := false
			for _, l := range lanes {
				in = in || inRect(l.Area, c)
			}
			if !in {
				t.Fatal("wind cell outside the lanes", rot, c)
			}
		}
	}
	if !rots[domain.East] || !rots[domain.West] {
		t.Fatal(rots)
	}
}
