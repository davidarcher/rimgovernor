package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func recoverSite(region Region) StoreSite {
	return StoreSite{Role: domain.GeneralRole, Interior: region.Bounds(), Region: region, Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
}

// A shrunk room's zone loses only the cells the room gave up.
func TestStoreShrinkRemovesOnlyGivenUpCells(t *testing.T) {
	t.Parallel()
	old := Rectangle{X: 4, Z: 4, Width: 5, Height: 3}
	zone := StockpileZone{ID: "Zone_1", Role: domain.GeneralRole, Cells: rectCells(old), Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
	site := recoverSite(RectRegion(Rectangle{X: 4, Z: 4, Width: 3, Height: 3}))
	edits := storeShrinkEdits([]StockpileZone{zone}, []StoreSite{site}, sitingOpen(), nil)
	if len(edits) != 1 || edits[0].Kind != StockpileShrink || edits[0].Zone != "Zone_1" || len(edits[0].RemovedCells) != 6 {
		t.Fatalf("shrink %+v, want the 6 cells of columns 7 and 8", edits)
	}
	for _, c := range edits[0].RemovedCells {
		if c.X < 7 {
			t.Fatalf("shrink removed %v, still inside the room", c)
		}
	}
	if got := storeSiteMoves([]StockpileZone{zone}, []StoreSite{site}); len(got) != 0 {
		t.Fatalf("a shrunk zone is not moved: %+v", got)
	}
}

// A grown room's standing zone is filled over the new floor, region-shaped.
func TestStoreGrowFollowsTheRegion(t *testing.T) {
	t.Parallel()
	zone := StockpileZone{ID: "Zone_1", Role: domain.GeneralRole, Cells: rectCells(Rectangle{X: 4, Z: 4, Width: 3, Height: 3})}
	site := recoverSite(RectRegion(Rectangle{X: 4, Z: 4, Width: 4, Height: 3}))
	open := sitingOpen()
	for c := range open.cells {
		sc := open.cells[c]
		sc.Zone = domain.Known(zone.Cells != nil && cellSet(zone.Cells)[c])
		open.cells[c] = sc
	}
	edits := storeGeometryEdits([]StockpileZone{zone}, []StoreSite{site}, open, nil)
	if len(edits) != 1 || edits[0].Kind != StockpileGrow || len(edits[0].AddedCells) != 3 {
		t.Fatalf("grow %+v, want the 3 cells of column 7", edits)
	}
}

// A replaced room re-points the zone: the successor's zone is created over the
// region cell for cell with the declared filter and priority, and the old zone
// is deleted only after it stands.
func TestStoreReplacedRoomRepointsTheZone(t *testing.T) {
	t.Parallel()
	old := StockpileZone{ID: "Zone_old", Role: domain.GeneralRole, Cells: rectCells(Rectangle{X: 1, Z: 1, Width: 2, Height: 2}), Filter: domain.GeneralFilter(), Priority: domain.LowPriority}
	region := RegionOf(append(rectCells(Rectangle{X: 8, Z: 8, Width: 4, Height: 2}), rectCells(Rectangle{X: 8, Z: 10, Width: 2, Height: 2})...))
	site := recoverSite(region)
	if !site.reshaped() {
		t.Fatal("test region must be an L")
	}
	deletes := storeSiteMoves([]StockpileZone{old}, []StoreSite{site})
	if len(deletes) != 1 || deletes[0].After != domain.GeneralRole {
		t.Fatalf("delete %+v, want it to wait on the create", deletes)
	}
	creates := storeSiteEdits([]StockpileZone{old}, []StoreSite{site}, sitingOpen())
	if len(creates) != 1 || creates[0].Kind != StockpileCreate || len(creates[0].CellList) != 12 || creates[0].Filter != old.Filter || creates[0].Priority != old.Priority {
		t.Fatalf("create %+v, want one 12-cell L carrying the old filter and priority", creates)
	}
	if _, err := domain.NewFilteredStockpileCellsZone(creates[0].Filter, creates[0].Priority, creates[0].Cells()); err != nil {
		t.Fatal(err)
	}
}

// Re-covering leaves furniture out: the create skips its cells and a standing
// zone sheds them, while a shelf is the zone's own storage and stays.
func TestStoreRecoverLeavesFurnitureOut(t *testing.T) {
	t.Parallel()
	room := Rectangle{X: 4, Z: 4, Width: 3, Height: 3}
	bench, shelf := domain.Cell{X: 5, Z: 5}, domain.Cell{X: 6, Z: 5}
	site := recoverSite(RectRegion(room))
	open := sitingOpen(bench)
	sc := open.cells[shelf]
	sc.Things = []Thing{{Def: ShelfDefinition, Category: ThingBuilding, Flags: FlagEdifice, Count: 1, Building: &BuildingState{}}}
	open.cells[shelf] = sc
	zone := StockpileZone{ID: "Zone_1", Role: domain.GeneralRole, Cells: rectCells(room)}
	edits := storeShrinkEdits([]StockpileZone{zone}, []StoreSite{site}, open, nil)
	if len(edits) != 1 || len(edits[0].RemovedCells) != 1 || edits[0].RemovedCells[0] != bench {
		t.Fatalf("shrink %+v, want only the bench cell", edits)
	}
	fresh := storeSiteEdits(nil, []StoreSite{site}, open)
	if len(fresh) != 1 || cellSet(fresh[0].Cells())[bench] || len(fresh[0].Cells()) != 7 {
		t.Fatalf("create %+v must leave the bench out", fresh)
	}
}
