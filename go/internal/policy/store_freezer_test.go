package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func freezerView() (StoreView, PlannedRoom) {
	kitchen := PlannedRoom{Role: PlannedKitchen, Interior: Rectangle{X: 20, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 22, Z: 15}, DoorRot: domain.South}
	link := domain.Cell{X: 19, Z: 12}
	freezer := PlannedRoom{Role: PlannedFreezer, Interior: Rectangle{X: 12, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 14, Z: 15}, DoorRot: domain.South, Link: &link}
	return StoreView{Layout: &LayoutPlan{Rooms: []PlannedRoom{kitchen, freezer}}}, freezer
}

// The freezer's stores are declared from the planned room at plan time, on
// open ground before walls or roof: three Critical 2x2 shelves and a Preferred
// perishables cover over the room, with no room census.
func TestFreezerStoresDeclaredFromThePlannedRoom(t *testing.T) {
	t.Parallel()
	view, freezer := freezerView()
	creates := storeCreates(view)
	for _, prefix := range []string{domain.RawMeatRolePrefix, domain.RawVegRolePrefix, domain.CorpsesRolePrefix} {
		edit, ok := creates[plannedKey(prefix, freezer.Interior)]
		if !ok || len(edit.Cells) != 4 || edit.Priority != domain.CriticalPriority || !withinRect(edit.Cells, freezer.Interior) {
			t.Fatalf("%s shelf: %+v", prefix, edit)
		}
	}
	perishables, ok := creates[plannedKey(domain.PerishablesRolePrefix, freezer.Interior)]
	if !ok || perishables.Priority != domain.PreferredPriority || !withinRect(perishables.Cells, freezer.Interior) {
		t.Fatalf("perishables: %+v", perishables)
	}
	if got := storeCreates(StoreView{Layout: &LayoutPlan{}}); len(got) != 0 {
		t.Fatalf("no freezer planned %+v", got)
	}
}

// A freezer interior still holding rock to dig defers every freezer create
// until it is settled (#2190); a blocked non-rock cell is kept out of the
// perishables cover instead.
func TestFreezerStoresDeferWhileTheInteriorIsNotOpen(t *testing.T) {
	t.Parallel()
	view, freezer := freezerView()
	view.Bounds = Bounds{Width: 40, Height: 40}
	plan := func(mark func(*SiteCell)) []StockpileEdit {
		view.Cells = unroofedGround()
		for i, c := range view.Cells {
			if c.Cell.X == 15 && c.Cell.Z == 12 {
				mark(&view.Cells[i])
			}
		}
		return PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Stores: DeclareStores(view).Stores}).Edits
	}
	freezerCreate := func(edits []StockpileEdit) (n int) {
		for _, e := range edits {
			if e.Kind == StockpileCreate && withinRect(e.Cells, freezer.Interior) {
				n++
			}
		}
		return n
	}
	rock := plan(func(c *SiteCell) {
		c.Walkable, c.Occupied, c.NaturalRock = domain.Known(false), domain.Known(true), domain.Known(true)
	})
	if n := freezerCreate(rock); n != 0 {
		t.Fatalf("%d freezer creates over rock still to dig", n)
	}
	blocked := plan(func(c *SiteCell) { c.Walkable = domain.Known(false) })
	if n := freezerCreate(blocked); n != 4 {
		t.Fatalf("%d freezer creates with a kept cell, want 4", n)
	}
}
