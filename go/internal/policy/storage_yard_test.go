package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// yardField is a 100x100 open map with a workshop and a storeroom standing
// as roofed rooms; zoned cells are flagged for the maintenance pass.
func yardField(zoned map[domain.Cell]bool) (StorageRequest, Rectangle) {
	workshop := PlannedRoom{Role: PlannedWorkshop, Interior: Rectangle{X: 40, Z: 40, Width: 7, Height: 5}}
	storage := PlannedRoom{Role: PlannedStorage, Interior: Rectangle{X: 52, Z: 40, Width: 5, Height: 5}}
	layout := LayoutPlan{Rooms: []PlannedRoom{workshop, storage}}
	var cells []SiteCell
	rooms := RoomObservation{Shapes: testShapes}
	for i, r := range layout.Rooms {
		rooms.Rooms = append(rooms.Rooms, Room{ID: string(rune('a' + i)), Enclosed: domain.Known(true), Cells: rectCells(r.Interior)})
	}
	roofed := map[domain.Cell]bool{}
	for _, room := range rooms.Rooms {
		for _, c := range room.Cells {
			roofed[c] = true
		}
	}
	for _, c := range rectCells(Rectangle{Width: 100, Height: 100}) {
		cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zoned[c]),
			Roofed: domain.Known(roofed[c]), Indoors: domain.Known(roofed[c]), StorageEmpty: domain.Known(true)})
	}
	return StorageRequest{Bounds: Bounds{Width: 100, Height: 100}, Cells: cells, Layout: &layout, Rooms: &rooms}, workshop.Interior
}

func yardSite(t *testing.T, r StorageRequest) StockpileSite {
	t.Helper()
	for _, site := range PlanStorage(r).Sites {
		if site.Role == domain.YardRole {
			return site
		}
	}
	t.Fatal("no yard site planned")
	return StockpileSite{}
}

// The yard is an unroofed 2x2 inside the inner ring, nearest the workshop
// by walking distance, at Low priority on the outdoor-safe filter.
func TestPlanStorageYardIsUnroofedInsideTheRingNearestTheWorkshop(t *testing.T) {
	t.Parallel()
	r, workshop := yardField(nil)
	site := yardSite(t, r)
	if site.Priority != domain.LowPriority || site.Filter != domain.YardFilter() || len(site.Candidates) == 0 {
		t.Fatalf("%+v", site)
	}
	enclosure := coreEnclosure(*r.Layout, 100, 100)
	inRing := func(c domain.Cell) bool { return enclosure.in[c.Z*100+c.X] }
	roofed := map[domain.Cell]bool{}
	for _, room := range r.Rooms.Rooms {
		for _, c := range room.Cells {
			roofed[c] = true
		}
	}
	for _, c := range site.Room {
		if !inRing(c) || roofed[c] {
			t.Fatalf("site room cell %v is outside the ring or roofed", c)
		}
	}
	for _, block := range site.Candidates {
		for _, c := range block {
			if !inRing(c) || roofed[c] {
				t.Fatalf("candidate cell %v is outside the ring or roofed", c)
			}
		}
	}
	first := site.Candidates[0]
	if len(first) != 4 {
		t.Fatalf("first candidate %v", first)
	}
	touching := false
	for _, c := range first {
		touching = touching || contains(pad(workshop, 1), c) || contains(pad(workshop, 2), c)
	}
	if !touching {
		t.Fatalf("first candidate %v is not beside the workshop %+v", first, workshop)
	}
	// No standing workshop, no yard.
	r.Rooms = &RoomObservation{Shapes: testShapes}
	for _, s := range PlanStorage(r).Sites {
		if s.Role == domain.YardRole {
			t.Fatalf("yard planned without a workshop: %+v", s)
		}
	}
}

// A full yard grows, but only onto unroofed ground inside the ring.
func TestYardGrowsOnlyInsideTheRing(t *testing.T) {
	t.Parallel()
	r, _ := yardField(nil)
	room := yardSite(t, r).Room
	edge := room[0]
	for _, c := range room {
		if c.X > edge.X || c.X == edge.X && c.Z < edge.Z {
			edge = c
		}
	}
	zoneCells := rectCells(Rectangle{X: edge.X - 1, Z: edge.Z, Width: 2, Height: 2})
	zoned := map[domain.Cell]bool{}
	for _, c := range zoneCells {
		zoned[c] = true
	}
	r, _ = yardField(zoned)
	site := yardSite(t, r)
	inRoom := cellSet(site.Room)
	review := PlanStockpileMaintenance(StockpileRequest{
		Tick: 100000, Bounds: r.Bounds, Cells: r.Cells, Colonists: domain.Known(int64(3)), Sited: []StockpileSite{site},
		Zones: []StockpileZone{{ID: "Zone_1", Role: domain.YardRole, Cells: zoneCells, Stored: zoneCells, Filter: site.Filter, Priority: site.Priority}},
		Roles: func(string) (StockpileRoleState, bool) {
			return StockpileRoleState{Filter: site.Filter, Priority: site.Priority}, true
		},
	})
	if len(review.Edits) != 1 || review.Edits[0].Kind != StockpileGrow {
		t.Fatalf("review %+v", review)
	}
	for _, c := range review.Edits[0].Cells {
		if !inRoom[c] {
			t.Fatalf("grew onto %v outside the yard ground", c)
		}
	}
}
