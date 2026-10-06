package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// foodSiteRequests is the fresh-colony ground of the opening tests with a
// food store anchored at the kitchen.
func foodSiteRequests(kitchen domain.Cell) (StockpileRequest, StorageRequest) {
	r := freshColonyStockpiles()
	s := StorageRequest{Bounds: r.Bounds, Cells: r.Cells, Food: &FoodStore{Anchor: kitchen}}
	return r, s
}

func foodSiteCreate(t *testing.T, r StockpileRequest) StockpileEdit {
	t.Helper()
	var found StockpileEdit
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Role == domain.FoodRole {
			found = e
		}
	}
	if len(found.Cells) != 9 {
		t.Fatalf("no food stockpile: %+v", PlanStockpileMaintenance(r).Edits)
	}
	return found
}

// The opening food zone is the Food department's: a 3x3 Preferred food stockpile on
// the only roofed floor, created in the colony's first review.
func TestFoodSiteIsSitedOnRoofedFloorLikeTheOpening(t *testing.T) {
	r, s := foodSiteRequests(domain.Cell{X: 10, Z: 10})
	r.Stores = DeclareStores(s).Stores
	food := foodSiteCreate(t, r)
	if food.Kind != StockpileCreate || food.Filter != domain.FoodFilter() || food.Priority != domain.PreferredPriority {
		t.Fatalf("food %+v", food)
	}
	for _, c := range food.Cells {
		if c.X < 30 || c.X >= 34 || c.Z < 30 || c.Z >= 34 {
			t.Fatalf("food not on the roofed floor: %v", food.Cells)
		}
	}
}

// With no roofed floor the food stockpile sits beside the kitchen.
func TestFoodSiteFollowsTheKitchenBeforeAnyRoof(t *testing.T) {
	r, s := foodSiteRequests(domain.Cell{X: 20, Z: 5})
	for i := range r.Cells {
		r.Cells[i].Roofed = domain.Known(false)
	}
	s.Cells = r.Cells
	r.Stores = DeclareStores(s).Stores
	food := foodSiteCreate(t, r)
	for _, c := range food.Cells {
		if absInt32(c.X-20) > 2 || absInt32(c.Z-5) > 2 {
			t.Fatalf("food not beside the kitchen: %v", food.Cells)
		}
	}
}

// A roofed block outside the bedrooms beats a nearer one inside a bedroom.
func TestFoodSitePrefersRoofedFloorOutsideTheBedrooms(t *testing.T) {
	r, s := foodSiteRequests(domain.Cell{X: 31, Z: 31})
	for i, c := range r.Cells {
		r.Cells[i].Roofed = domain.Known(c.Cell.X >= 20 && c.Cell.X < 30 && c.Cell.Z >= 20 && c.Cell.Z < 34 || c.Cell.X >= 30 && c.Cell.X < 34 && c.Cell.Z >= 30 && c.Cell.Z < 34)
	}
	var bedroom []domain.Cell
	for x := int32(30); x < 34; x++ {
		for z := int32(30); z < 34; z++ {
			bedroom = append(bedroom, domain.Cell{X: x, Z: z})
		}
	}
	s.Cells = r.Cells
	s.Rooms = &RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "bed", Cells: bedroom, Role: domain.Known(RoomRoleBedroom)}}}
	r.Stores = DeclareStores(s).Stores
	for _, c := range foodSiteCreate(t, r).Cells {
		if c.X >= 30 {
			t.Fatalf("food inside the bedroom: %v", c)
		}
	}
}

// A food stockpile standing outdoors moves indoors once a roofed block is
// free (create first, then delete), and a zone already on roofed floor, or with
// no roofed block free, is never moved or doubled.
func TestFoodSiteMovesIndoorsOnceAndThenStays(t *testing.T) {
	r, s := foodSiteRequests(domain.Cell{X: 10, Z: 10})
	outdoor := []domain.Cell{{X: 5, Z: 5}, {X: 6, Z: 5}, {X: 7, Z: 5}, {X: 5, Z: 6}, {X: 6, Z: 6}, {X: 7, Z: 6}, {X: 5, Z: 7}, {X: 6, Z: 7}, {X: 7, Z: 7}}
	r.Zones = []StockpileZone{
		{ID: "gen", Role: domain.GeneralRole, Cells: []domain.Cell{{X: 0, Z: 0}}, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "dump", Role: domain.DumpRole, Cells: []domain.Cell{{X: 0, Z: 4}}, Filter: domain.DumpFilter(), Priority: domain.LowPriority},
		{ID: "food", Role: domain.FoodRole, Cells: outdoor, Filter: domain.FoodFilter(), Priority: domain.PreferredPriority},
	}
	r.Stores = DeclareStores(s).Stores
	var kinds []StockpileEditKind
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Role == domain.FoodRole {
			kinds = append(kinds, e.Kind)
		}
	}
	if len(kinds) != 2 || kinds[0] != StockpileCreate || kinds[1] != StockpileDelete {
		t.Fatalf("outdoor zone not moved indoors: %v", kinds)
	}
	roofed := []domain.Cell{{X: 30, Z: 30}, {X: 31, Z: 30}, {X: 32, Z: 30}, {X: 30, Z: 31}, {X: 31, Z: 31}, {X: 32, Z: 31}, {X: 30, Z: 32}, {X: 31, Z: 32}, {X: 32, Z: 32}}
	r.Zones[2].Cells = roofed
	for i, c := range r.Cells {
		for _, z := range roofed {
			if c.Cell == z {
				r.Cells[i].Zone = domain.Known(true)
			}
		}
	}
	s.Cells = r.Cells
	r.Stores = DeclareStores(s).Stores
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Role == domain.FoodRole || e.Zone == "food" {
			t.Fatalf("standing indoor zone touched: %+v", e)
		}
	}
}

// No food site is planned once the colony's food storage is met.
func TestFoodSiteIsAbsentWhenStorageIsMet(t *testing.T) {
	if stores := DeclareStores(StorageRequest{Bounds: Bounds{Width: 4, Height: 4}}).Stores; len(stores) != 0 {
		t.Fatalf("%+v", stores)
	}
}
