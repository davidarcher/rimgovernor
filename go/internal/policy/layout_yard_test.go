package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// The yard is a planned Outdoor room, 13x9 inside, beside the core and inside
// its ring; a second is added only on demand and nothing placed moves.
func TestYardIsAPlannedOutdoorRoomInsideTheRing(t *testing.T) {
	t.Parallel()
	plan := PlanYardSites(gearTestPlan(), 1)
	yards := plan.YardRooms()
	if len(yards) != 1 {
		t.Fatalf("%d yards, want 1", len(yards))
	}
	yard := yards[0]
	if yard.Role != PlannedYard || !yard.Outdoor || yard.Interior.Width != YardW || yard.Interior.Height != YardH || len(rectCells(yard.Interior)) != 117 {
		t.Fatalf("yard %+v", yard)
	}
	if !innerEnclosed[ReserveYard] || !PlannedYard.IsOutdoor() {
		t.Fatal("the yard is not enclosed by the core ring as an Outdoor room")
	}
	enclosure := coreEnclosure(plan, 160, 160)
	for _, c := range rectCells(pad(yard.Interior, 1)) {
		if !enclosure.inside(c) {
			t.Fatalf("yard cell %v outside the core enclosure", c)
		}
	}
	if again := PlanYardSites(plan, 1); len(again.Reservations) != len(plan.Reservations) {
		t.Fatal("a yard the plan holds is added again")
	}
	two := PlanYardSites(plan, 2)
	if len(two.YardRooms()) != 2 || two.YardRooms()[0].Interior != yard.Interior {
		t.Fatalf("further yard moved the first: %+v", two.YardRooms())
	}
	if owed := YardRoomsOwed(plan, RoomDemand{Yard: 2}); owed != 1 {
		t.Fatalf("owed %d, want 1", owed)
	}
	if owed := YardRoomsOwed(LayoutPlan{}, RoomDemand{}); owed != 1 {
		t.Fatalf("a plan with no yard owes %d, want 1", owed)
	}
}

// The yard store covers the whole yard interior as one zone: outdoor_safe, Low
// priority, created at plan time, never resized, and a full yard asks for a
// further one that gets its own zone once planned.
func TestYardStoreCoversItsWholeRoom(t *testing.T) {
	t.Parallel()
	plan := PlanYardSites(gearTestPlan(), 1)
	req := yardStorageRequest(plan)
	stores := storageOwner{}.Stores(req)
	var yard Store
	for _, s := range stores {
		if s.Role == domain.YardRole {
			yard = s
		}
	}
	if yard.Role == "" || yard.Interior != plan.YardRooms()[0].Interior || yard.Filter != domain.YardFilter() || yard.Priority != domain.LowPriority || yard.Further != PlannedYard {
		t.Fatalf("yard store %+v", yard)
	}
	created := createdRoles(PlanStockpileMaintenance(stockpileOf(req)))
	zone := created[domain.YardRole]
	if len(zone.Cells()) != 117 || zone.Filter.Base() != domain.BaseOutdoorSafe || zone.Priority != domain.LowPriority {
		t.Fatalf("yard zone %d cells, filter %v, priority %v", len(zone.Cells()), zone.Filter.Base(), zone.Priority)
	}
	cells := rectCells(yard.Interior)
	standing := StockpileZone{ID: "yard", Role: domain.YardRole, Cells: cells, Stored: cells[:100], Filter: domain.YardFilter(), Priority: domain.LowPriority}
	req.Zones = []StockpileZone{standing}
	for _, e := range PlanStockpileMaintenance(stockpileOf(req)).Edits {
		if e.Role == domain.YardRole {
			t.Fatalf("a standing yard zone was edited: %+v", e)
		}
	}
	if got := storageDemand(req).Yard; got != 2 {
		t.Fatalf("a yard at %d/117 wants %d yards, want 2", 100, got)
	}
	standing.Stored = cells[:50]
	req.Zones = []StockpileZone{standing}
	if got := storageDemand(req).Yard; got != 0 {
		t.Fatalf("a yard with room wants %d yards", got)
	}
	// The further yard stands: its zone is created, and no third is asked for
	// until it fills too.
	standing.Stored = cells[:100]
	req = yardStorageRequest(PlanYardSites(plan, 2))
	req.Zones = []StockpileZone{standing}
	if got := storageDemand(req).Yard; got != 0 {
		t.Fatalf("a planned further yard answers the demand: %d", got)
	}
	created = createdRoles(PlanStockpileMaintenance(stockpileOf(req)))
	further := created[domain.YardRole]
	if len(further.Cells()) != 117 || further.Cells()[0] == cells[0] {
		t.Fatalf("further yard zone %+v", further)
	}
}

// yardStorageRequest is the storage view of a plan on open ground.
func yardStorageRequest(plan LayoutPlan) StoreView {
	var cells []SiteCell
	for x := int32(0); x < 160; x++ {
		for z := int32(0); z < 160; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: OccupantThings(false), Zone: domain.Known(false), Roofed: domain.Known(false), Indoors: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return StoreView{Bounds: Bounds{Width: 160, Height: 160}, Cells: cells, Layout: &plan, Rooms: &RoomObservation{Shapes: testShapes}}
}

// A fresh plan holds one yard.
func TestDerivedPlanHoldsAYard(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	plan, ok := DeriveLayoutPlan(plusSurvey(140, 0), 8, TechTierCamp, nil, 0, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	if got := len(plan.YardRooms()); got != 1 {
		t.Fatalf("%d yards in a fresh plan, want 1", got)
	}
}

// The yard's fence ring is owed until every ring cell holds a fence and the
// gate stands toward the core (#2215); the ring is outside the interior, so
// raising it never breaks the yard's store zone.
func TestYardRingIsOwedUntilFenceAndGateStand(t *testing.T) {
	t.Parallel()
	plan := PlanYardSites(gearTestPlan(), 1)
	yard := plan.YardRooms()[0]
	if _, owed := plan.NextPlannedRoom(PlannedYard, GroundOf(nil)); !owed {
		t.Fatal("a yard with no ring is not owed")
	}
	gates := map[domain.Cell]bool{}
	for _, c := range plan.ShellDoors(yard) {
		gates[c] = true
	}
	var buildings []CurrentBuilding
	ring := roomWalls(yard)
	for _, c := range rectCells(ring) {
		if !onRing(c, ring) {
			continue
		}
		if c.X >= yard.Interior.X && c.X < yard.Interior.X+yard.Interior.Width && c.Z >= yard.Interior.Z && c.Z < yard.Interior.Z+yard.Interior.Height {
			t.Fatalf("ring cell %v inside the yard interior", c)
		}
		def := PenFenceDefinition
		if gates[c] {
			def = PenGateDefinition
		}
		b, err := domain.NewBuilding(def, c, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		buildings = append(buildings, CurrentBuilding{Building: b, Cells: []domain.Cell{c}})
	}
	if len(gates) == 0 {
		t.Fatal("the yard plans no gate")
	}
	if room, owed := plan.NextPlannedRoom(PlannedYard, GroundOf(buildings)); owed {
		t.Fatalf("yard still owed with its ring standing: %+v", room)
	}
}
