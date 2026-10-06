package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// sitingOpen is a 20x20 open map; blocked cells are occupied.
func sitingOpen(blocked ...domain.Cell) stockpileOpen {
	r := StockpileRequest{Bounds: Bounds{Width: 20, Height: 20}}
	skip := cellSet(blocked)
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			c := domain.Cell{X: x, Z: z}
			r.Cells = append(r.Cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(skip[c]), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return newStockpileOpen(r)
}

func TestStoreSiteCoversTheWholeInterior(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: Rectangle{X: 4, Z: 4, Width: 5, Height: 3}}
	if cells := site.Cells(sitingOpen()); len(cells) != 15 {
		t.Fatalf("cover %d cells, want 15: %v", len(cells), cells)
	}
}

func TestStoreSiteSkipsBuildingAndBlockedCells(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: domain.GeneralRole, Interior: Rectangle{X: 4, Z: 4, Width: 5, Height: 3}}
	bench := domain.Cell{X: 5, Z: 5}
	cells := site.Cells(sitingOpen(bench))
	if len(cells) != 14 || cellSet(cells)[bench] {
		t.Fatalf("cover %v must skip the building cell", cells)
	}
	if cellSet(cells)[domain.Cell{X: 3, Z: 4}] {
		t.Fatalf("cover reached the wall ring: %v", cells)
	}
}

func TestStoreSiteRectangleIsCleanAndNearAnchor(t *testing.T) {
	t.Parallel()
	site := StoreSite{Role: "food", Interior: Rectangle{X: 2, Z: 2, Width: 8, Height: 6}, Width: 2, Height: 2, Anchor: domain.Cell{X: 9, Z: 7}}
	got := site.Cells(sitingOpen())
	if len(got) != 4 || got[0] != (domain.Cell{X: 8, Z: 6}) {
		t.Fatalf("rectangle %v, want the 2x2 at the anchor corner (8,6)", got)
	}
	// A building under the anchor corner pushes the patch to the next clean one.
	got = site.Cells(sitingOpen(domain.Cell{X: 9, Z: 7}))
	for _, c := range got {
		if c == (domain.Cell{X: 9, Z: 7}) {
			t.Fatalf("rectangle covers a building: %v", got)
		}
	}
	if len(got) != 4 {
		t.Fatalf("rectangle %v", got)
	}
}

// RimWorld renumbers census rooms: a zone whose role carries another room ID
// still serves the site standing on the same interior.
func TestStoreSiteMatchesByPositionAcrossRenumbering(t *testing.T) {
	t.Parallel()
	interior := Rectangle{X: 4, Z: 4, Width: 5, Height: 3}
	site := StoreSite{Role: "general:Room_9", Interior: interior}
	zone := StockpileZone{ID: "Zone_1", Role: "general:Room_7", Cells: rectCells(interior)}
	if !site.serves(zone) {
		t.Fatal("renumbered zone not matched to its site")
	}
	if edits := storeSiteMoves([]StockpileZone{zone}, []StoreSite{site}); len(edits) != 0 {
		t.Fatalf("renumbered zone moved: %+v", edits)
	}
	if edits := storeSiteEdits([]StockpileZone{zone}, []StoreSite{site}, sitingOpen()); len(edits) != 0 {
		t.Fatalf("served site created again: %+v", edits)
	}
}

// A moved zone's delete waits on the replacement's create (After names the
// site) until a zone serves the new site, and the create comes first.
func TestStoreSiteMoveCreatesBeforeDeleting(t *testing.T) {
	t.Parallel()
	old := StockpileZone{ID: "Zone_old", Role: "general", Cells: []domain.Cell{{X: 1, Z: 1}}}
	site := StoreSite{Role: "general", Interior: Rectangle{X: 4, Z: 4, Width: 3, Height: 3}}
	deletes := storeSiteMoves([]StockpileZone{old}, []StoreSite{site})
	if len(deletes) != 1 || deletes[0].Kind != StockpileDelete || deletes[0].After != "general" {
		t.Fatalf("delete %+v, want it to wait on the create of general", deletes)
	}
	creates := storeSiteEdits([]StockpileZone{old}, []StoreSite{site}, sitingOpen())
	if len(creates) != 1 || creates[0].Kind != StockpileCreate || len(creates[0].Cells) != 9 {
		t.Fatalf("create %+v", creates)
	}
	fresh := StockpileZone{ID: "Zone_new", Role: "general", Cells: creates[0].Cells}
	deletes = storeSiteMoves([]StockpileZone{old, fresh}, []StoreSite{site})
	if len(deletes) != 1 || deletes[0].Zone != "Zone_old" || deletes[0].After != "" {
		t.Fatalf("delete %+v once the replacement stands, want it unconditional", deletes)
	}
}

// Two stores sited in one pass never share a cell: the second takes what the
// first left, or nothing.
func TestStoreSitesInOnePassNeverOverlap(t *testing.T) {
	t.Parallel()
	room := Rectangle{X: 4, Z: 4, Width: 4, Height: 2}
	sites := []StoreSite{
		{Role: "a", Interior: room, Width: 2, Height: 2, Anchor: domain.Cell{X: 4, Z: 4}},
		{Role: "b", Interior: room, Width: 2, Height: 2, Anchor: domain.Cell{X: 4, Z: 4}},
		{Role: "c", Interior: room, Width: 2, Height: 2, Anchor: domain.Cell{X: 4, Z: 4}},
	}
	edits := storeSiteEdits(nil, sites, sitingOpen())
	if len(edits) != 2 {
		t.Fatalf("%d creates, want 2 (the room holds two 2x2): %+v", len(edits), edits)
	}
	seen := map[domain.Cell]string{}
	for _, e := range edits {
		for _, c := range e.Cells {
			if other, dup := seen[c]; dup {
				t.Fatalf("%s overlaps %s at %v", e.Role, other, c)
			}
			seen[c] = e.Role
		}
	}
}
