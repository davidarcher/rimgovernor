package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRawFoodStockSitsInTheFreezerAtTheKitchenDoor(t *testing.T) {
	t.Parallel()
	// A 5x5 freezer interior at (10..14, 10..14); its outer door is on the
	// far (east) wall, its Link into the kitchen on the west wall at (9,12).
	link := domain.Cell{X: 9, Z: 12}
	freezer := policy.LayoutRoom{Role: policy.ModuleFreezer, Interior: policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 15, Z: 12}, Link: &link}
	var cells []policy.SiteCell
	var roomCells []domain.Cell
	for x := int32(0); x < 30; x++ {
		for z := int32(0); z < 30; z++ {
			cell := domain.Cell{X: x, Z: z}
			cells = append(cells, policy.SiteCell{Cell: cell, Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
			if x >= 10 && x < 15 && z >= 10 && z < 15 {
				roomCells = append(roomCells, cell)
			}
		}
	}
	rooms := policy.RoomObservation{Rooms: []policy.Room{{ID: "freezer", Role: domain.Known(policy.RoomRoleStoreroom), Enclosed: domain.Known(true), Cells: roomCells}}}
	bounds := policy.Bounds{Width: 30, Height: 30}
	layout := policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleKitchen, Interior: policy.Rectangle{X: 3, Z: 10, Width: 5, Height: 5}}, freezer}}
	room, sites, err := rawFoodStockSites(layout, rooms, bounds, cells, nil)
	if err != nil || room != "freezer" || len(sites) == 0 || len(sites[0]) != 4 {
		t.Fatal(room, sites, err)
	}
	for _, c := range sites[0] {
		if c.X < 10 || c.X > 11 || c.Z < 11 || c.Z > 13 {
			t.Fatal("stock must sit inside the freezer against the kitchen door wall", sites[0])
		}
	}
	// Without a Link (plans before #819) the outer door anchors the stock.
	freezer.Link = nil
	layout.Rooms[1] = freezer
	_, outer, err := rawFoodStockSites(layout, rooms, bounds, cells, nil)
	if err != nil || len(outer) == 0 || outer[0][0].X < 13 {
		t.Fatal(outer, err)
	}
	// No standing freezer means no stock.
	if room, none, err := rawFoodStockSites(layout, policy.RoomObservation{}, bounds, cells, nil); err != nil || room != "" || none != nil {
		t.Fatal(room, none, err)
	}
}
