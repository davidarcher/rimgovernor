package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func incineratorCensus(w, h int32) []SiteCell {
	return dumpCensus(w, h, func(domain.Cell) bool { return false })
}

func livingRoom(x0, z0 int32) Room {
	var cells []domain.Cell
	for x := x0; x < x0+3; x++ {
		for z := z0; z < z0+3; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return Room{ID: "Room_1", Role: domain.Known(RoomRoleBedroom), Cells: cells}
}

// The incinerator stands on open ground beside the dump anchor, its whole
// walled outline clear of living rooms by the dumps' six cells, off the
// plan's rooms, with its door in the wall facing the dump patch.
func TestSiteIncineratorBesideDumpsClearOfLivingRooms(t *testing.T) {
	bedroom := livingRoom(10, 10)
	plan := LayoutPlan{Rooms: []LayoutRoom{{Role: ModuleStorage, Interior: Rectangle{X: 20, Z: 14, Width: 4, Height: 4}}}}
	req := IncineratorSiteRequest{Plan: plan, Bounds: Bounds{Width: 40, Height: 30}, Cells: incineratorCensus(40, 30), Rooms: []Room{bedroom}, Anchor: domain.Cell{X: 12, Z: 22}, Toward: domain.Cell{X: 12, Z: 22}}
	site, ok := SiteIncinerator(req)
	if !ok || site.Area.Width != 5 || site.Area.Height != 5 {
		t.Fatal(site, ok)
	}
	walls := map[domain.Cell]bool{}
	for _, c := range rectCells(roomWalls(plan.Rooms[0])) {
		walls[c] = true
	}
	for _, c := range rectCells(site.Area) {
		if c.X > 4 && c.X < 18 && c.Z > 4 && c.Z < 18 {
			t.Fatal("outline within the bedroom clearance", site.Area)
		}
		if walls[c] {
			t.Fatal("outline on a planned room", site.Area)
		}
	}
	room := site.Room()
	if room.Interior.Width != 3 || room.Interior.Height != 3 || room.Role != ModuleIncinerator {
		t.Fatal(room)
	}
	// The door sits in the middle of the wall that faces the dump anchor.
	in := room.Interior
	want := map[domain.Rotation]domain.Cell{
		domain.North: {X: in.X + 1, Z: in.Z + 3}, domain.South: {X: in.X + 1, Z: in.Z - 1},
		domain.East: {X: in.X + 3, Z: in.Z + 1}, domain.West: {X: in.X - 1, Z: in.Z + 1},
	}
	if room.Door != want[site.Facing] || room.DoorRot != site.Facing {
		t.Fatal(site.Facing, room.Door, room.DoorRot)
	}
	for _, tc := range []struct {
		toward domain.Cell
		want   domain.Rotation
	}{{domain.Cell{X: site.Area.X + 2, Z: 29}, domain.North}, {domain.Cell{X: site.Area.X + 2, Z: 0}, domain.South}, {domain.Cell{X: 39, Z: site.Area.Z + 2}, domain.East}, {domain.Cell{X: 0, Z: site.Area.Z + 2}, domain.West}} {
		if got := facingToward(site.Area, tc.toward); got != tc.want {
			t.Errorf("toward %v: %s, want %s", tc.toward, got, tc.want)
		}
	}
	// Ground crowded by living rooms on every side leaves no site.
	req.Rooms = nil
	for x := int32(0); x < 40; x += 8 {
		for z := int32(0); z < 30; z += 8 {
			req.Rooms = append(req.Rooms, livingRoom(x, z))
		}
	}
	if site, ok := SiteIncinerator(req); ok {
		t.Fatal("sited in the clearance", site)
	}
}

func TestIncineratorReservationIsPermanentAndSingle(t *testing.T) {
	site := IncineratorSite{Area: Rectangle{X: 30, Z: 4, Width: 5, Height: 5}, Facing: domain.West}
	plan, added := growIncinerator(LayoutPlan{}, site)
	if !added || len(plan.IncineratorRooms()) != 1 {
		t.Fatal(plan, added)
	}
	// A second demand never adds or moves one.
	again, added := growIncinerator(plan, IncineratorSite{Area: Rectangle{X: 0, Z: 0, Width: 5, Height: 5}})
	if added || len(again.Reservations) != 1 || again.Reservations[0].Area != site.Area {
		t.Fatal(again, added)
	}
	if _, added = growIncinerator(LayoutPlan{}, IncineratorSite{}); added {
		t.Fatal("added a zero site")
	}
	room := plan.IncineratorRooms()[0]
	if room.Interior != (Rectangle{X: 31, Z: 5, Width: 3, Height: 3}) || room.Door != (domain.Cell{X: 30, Z: 6}) || room.DoorRot != domain.West {
		t.Fatal(room)
	}
}

// Storage plans the incinerator once things wait for the dumps and the plan
// has none, and zones its interior only after its walls stand: a priority
// above the Low dumps, taking what the rotten and worn dumps take.
func TestPlanStorageIncinerator(t *testing.T) {
	needs := map[string]int{domain.RottenDumpRole: 2}
	req := StorageRequest{Bounds: Bounds{Width: 40, Height: 30}, Cells: incineratorCensus(40, 30), Layout: &LayoutPlan{}, Rooms: &RoomObservation{},
		Dumps: &DumpStore{Needs: needs, Anchor: domain.Cell{X: 10, Z: 10}}}
	plan := PlanStorage(req)
	if plan.Incinerator == (IncineratorSite{}) {
		t.Fatal("no incinerator owed")
	}
	for _, s := range plan.Sites {
		if s.Role == domain.IncineratorRole {
			t.Fatal("zone before the walls stand")
		}
	}
	// Nothing waiting for the rotten or worn dump, no demand.
	req.Dumps.Needs = map[string]int{domain.CorpseDumpRole: 4}
	if PlanStorage(req).Incinerator != (IncineratorSite{}) {
		t.Fatal("incinerator owed for the corpse dump alone")
	}
	// Once the plan holds one, it is not owed again; standing, it is zoned.
	req.Dumps.Needs = needs
	layout, _ := growIncinerator(LayoutPlan{}, plan.Incinerator)
	req.Layout = &layout
	if PlanStorage(req).Incinerator != (IncineratorSite{}) {
		t.Fatal("second incinerator owed")
	}
	room := layout.IncineratorRooms()[0]
	req.Dumps.Incinerator = &room
	var zone *StockpileSite
	for _, s := range PlanStorage(req).Sites {
		if s.Role == domain.IncineratorRole {
			zone = &s
		}
	}
	if zone == nil || len(zone.Room) != 9 || len(zone.Candidates) != 1 || len(zone.Candidates[0]) != 9 {
		t.Fatalf("incinerator zone: %+v", zone)
	}
	if zone.Priority != domain.PreferredPriority {
		t.Fatal(zone.Priority)
	}
	// It takes what the rotten and worn dumps take, but not serviceable gear.
	allow := domain.IncineratorFilter().Allow()
	for _, want := range []domain.FilterSelector{domain.CategoryDef("Apparel"), domain.CategoryDef("Weapons"), domain.CategoryDef("CorpsesAnimal"), domain.CategoryDef("Foods")} {
		found := false
		for _, a := range allow {
			found = found || a == want
		}
		if !found {
			t.Errorf("filter lacks %v", want)
		}
	}
	if _, top, ok := domain.IncineratorFilter().HitPoints(); !ok || top != domain.GearHitPointFloor {
		t.Fatal("serviceable gear would be burned", top, ok)
	}
}
