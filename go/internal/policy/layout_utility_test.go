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

// testTurbineSite is the turbine node's turn facing rot, placed at origin as the
// site embedding stores it.
func testTurbineSite(id string, rot domain.Rotation, origin domain.Cell) PlannedSite {
	node := turbineNode(id)
	for _, shape := range append([]SiteNode{node}, node.Alts...) {
		if shape.Rotation != rot {
			continue
		}
		p := SitePlacement{Node: id, Origin: origin, Footprint: shape.Footprint.Translate(origin.X, origin.Z), Rotation: rot}
		for _, l := range shape.Lanes {
			p.Lanes = append(p.Lanes, SiteLane{ID: l.ID, Region: l.Region.Translate(origin.X, origin.Z), Reason: l.Reason, Wind: true})
		}
		return PlannedSite{SitePlacement: p, Kind: NodeTurbine, Reason: ReasonPowerSite}
	}
	panic("no such turn")
}

// testSolarSite is a stored 4x4 solar plot.
func testSolarSite(id string, x, z int32) PlannedSite {
	return newSite(id, NodeSolar, RectRegion(Rectangle{X: x, Z: z, Width: solarSide, Height: solarSide}), ReasonPowerSite)
}

func TestPlannedPowerSites(t *testing.T) {
	p := PlanUtilities(withRooms(corePlan(utilityTestZones(), 3, TechTierCamp), PlannedBattery), UtilityWants{})
	p.Sites = append(p.Sites, testTurbineSite("wind-1", domain.North, domain.Cell{X: 60, Z: 60}), testTurbineSite("wind-2", domain.South, domain.Cell{X: 60, Z: 90}), testSolarSite("solar-1", 100, 100))
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

// TestBatteryRoomOnCrossing: with bedroom wings filling the main
// hallway the battery room still gets a site, on a crossing when needed,
// its slots nearest the door first and every block beside its battery.
func TestBatteryRoomOnCrossing(t *testing.T) {
	p := PlanUtilities(withRooms(corePlan(coreTestZones(), 12, TechTierCamp), PlannedBattery), UtilityWants{})
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
			if !inRect(room.Region.Bounds(), c) {
				t.Fatal("battery outside the room", room, b)
			}
		}
		if b.Block != (Rectangle{}) && !inRect(room.Region.Bounds(), domain.Cell{X: b.Block.X, Z: b.Block.Z}) {
			t.Fatal("block outside the room", room, b)
		}
	}
}

// TestBatterySlotsCrossing: an east-door room's slots are the north-door
// room's transposed, 1x2 along z, and plan north-facing batteries.
func TestBatterySlotsCrossing(t *testing.T) {
	room := PlannedRoom{Role: PlannedBattery, Region: RectRegion(Rectangle{X: 10, Z: 20, Width: 7, Height: 5}), Door: domain.Cell{X: 17, Z: 22}, DoorRot: domain.East}
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
// with the cold side in the room and the hot side on the exhaust,
// on either hallway: the freezer and the soil tomb both get one.
func TestPlannedCoolerSites(t *testing.T) {
	p := PlanUtilities(growPlan(corePlan(coreTestZones(), 0, TechTierCamp), 0, 1, TechTierCamp), UtilityWants{})
	sites := PlannedCoolerSites(p)
	if len(sites) != 2 {
		t.Fatal(sites)
	}
	for _, s := range sites {
		c := RefrigerationCooler{Position: s.Cell, Rotation: s.Rotation}
		var room *PlannedRoom
		for i, r := range p.Rooms {
			if inRect(r.Region.Bounds(), c.Cold()) {
				room = &p.Rooms[i]
			}
		}
		hot := false
		for _, r := range p.Reservations {
			hot = hot || r.Kind == ReserveExhaust && inRect(r.Area.Bounds(), c.Hot())
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
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(!wall), Things: OccupantThings(wall), Indoors: domain.Known(in), Roofed: domain.Known(in)})
		}
	}
	for x := int32(-1); x <= 5; x++ {
		for _, z := range []int32{-1, 5} {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: OccupantThings(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
		}
	}
	for z := int32(0); z < 5; z++ {
		for _, x := range []int32{-1, 5} {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: OccupantThings(false), Indoors: domain.Known(false), Roofed: domain.Known(false)})
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
			obs.Cells[i].SetNaturalRock(true) // back wall still rock
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
	core := withRooms(growPlan(corePlan(zones, 0, TechTierCamp), 0, 1, TechTierCamp), PlannedBattery)
	p := PlanUtilities(core, UtilityWants{Geysers: []Rectangle{{X: 20, Z: 190, Width: 2, Height: 2}}})
	if !p.WithRoomIDs().Valid() {
		t.Fatal("invalid")
	}
	count := map[ReservationKind]int{}
	for _, r := range p.Reservations {
		count[r.Kind]++
	}
	if count[ReserveGeothermal] != 1 || count[ReserveExhaust] != 2 {
		t.Fatal(count)
	}
	// Battery room: 5 wide off the spine, door on the walkway, no lane.
	var battery *PlannedRoom
	for i := range p.Rooms {
		if p.Rooms[i].Role == PlannedBattery {
			battery = &p.Rooms[i]
		}
	}
	if battery == nil || battery.Region.Bounds().Width != 5 || battery.Door.X != battery.Region.Bounds().X+2 {
		t.Fatal("battery", battery)
	}
	if n := len(BatterySlots(*battery)); n != 8 {
		t.Fatal("slots", n)
	}
	checkCore(t, LayoutPlan{Spine: p.Spine, Rooms: p.Rooms[:len(core.Rooms)], Zones: zones}, 0)
	// The generator never builds over a reservation.
	grown := growPlan(p, 20, 1, TechTierCamp)
	for _, r := range grown.Rooms {
		for _, res := range p.Reservations {
			w := roomWalls(r)
			if w.X < res.Area.Bounds().X+res.Area.Bounds().Width && res.Area.Bounds().X < w.X+w.Width && w.Z < res.Area.Bounds().Z+res.Area.Bounds().Height && res.Area.Bounds().Z < w.Z+w.Height {
				t.Fatal("room over reservation", r, res)
			}
		}
	}
}

func TestPlanUtilitiesHerdUnit(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	core := corePlan(utilityTestZones(), 3, TechTierCamp)
	p := PlanUtilities(core, UtilityWants{PenAnimals: 30})
	var barn *LayoutReservation
	for i, r := range p.Reservations {
		if r.Kind == ReserveBarn {
			barn = &p.Reservations[i]
		}
	}
	if barn == nil {
		t.Fatal("no barn")
	}
	if p.herdCapacity(PlannedBarn) < 30 {
		t.Fatal("barn too small", barn.Area)
	}
	// A herd unit's rooms share one wall line by design: the barn and
	// its vet room overlap in exactly that line, never in an interior.
	for _, r := range p.Reservations {
		if _, shared := sharedWallLink(r.Area.Bounds(), barn.Area.Bounds()); r.Kind != ReserveBarn && rectsOverlap(r.Area.Bounds(), barn.Area.Bounds()) && !shared {
			t.Fatal("barn overlaps", r)
		}
	}
	for _, r := range p.AllRooms() {
		if _, shared := sharedWallLink(roomWalls(r), barn.Area.Bounds()); rectsOverlap(roomWalls(r), barn.Area.Bounds()) && !shared {
			t.Fatal("barn overlaps room", r.Role)
		}
	}
	u := newUtilityGrid(core)
	if !u.free(barn.Area.Bounds(), true) {
		t.Fatal("barn in the hallway clearance", barn.Area)
	}
	// The site search follows the centre it is given.
	w, h := walledSide(herdSide(30))
	near, _ := u.site(w, h, false, false, 10, 10)
	far, _ := u.site(w, h, false, false, 70, 110)
	if near.X >= far.X && near.Z >= far.Z || near == far {
		t.Fatal("site ignores its centre", near, far)
	}
}

// TestStoredTurbineTurns: a stored turbine reads back in each of its four turns
// on its own footprint, the turned two as 2x7 pads facing east or west, and the
// wind lane it carries is exactly the game's wind path of the planned centre.
func TestStoredTurbineTurns(t *testing.T) {
	for i, rot := range []domain.Rotation{domain.North, domain.South, domain.East, domain.West} {
		p := LayoutPlan{Sites: []PlannedSite{testTurbineSite("wind-1", rot, domain.Cell{X: 40, Z: 50 + int32(i)})}}
		sites := PlannedPowerSites(p, WindTurbineDefinition)
		if len(sites) != 1 || sites[0].Rotation != rot {
			t.Fatal(rot, sites)
		}
		turned := rot == domain.East || rot == domain.West
		if a := sites[0].Area; turned != (a.Width == 2 && a.Height == turbineWidth) || !turned && (a.Width != turbineWidth || a.Height != 2) {
			t.Fatal("footprint", rot, a)
		}
		want := RegionOf(TurbineWindCells(sites[0].Cell, sites[0].Rotation))
		if !want.Equal(p.Sites[0].Lanes[0].Region) {
			t.Fatal("lane is not the wind path", rot)
		}
	}
}
