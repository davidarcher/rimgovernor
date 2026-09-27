package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestMealShelfSitsBesideTheDiningTableOffTheChairCells(t *testing.T) {
	t.Parallel()
	var cells []policy.SiteCell
	var roomCells []domain.Cell
	for x := int32(0); x < 30; x++ {
		for z := int32(0); z < 30; z++ {
			cell := domain.Cell{X: x, Z: z}
			// The 1x2 table at (15,15)-(15,16) occupies its cells.
			table := x == 15 && (z == 15 || z == 16)
			cells = append(cells, policy.SiteCell{Cell: cell, Roofed: domain.Known(true), Walkable: domain.Known(!table), Occupied: domain.Known(table), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
			if x >= 10 && x < 20 && z >= 10 && z < 20 {
				roomCells = append(roomCells, cell)
			}
		}
	}
	var adjacent []domain.Cell
	for x := int32(14); x <= 16; x++ {
		for z := int32(14); z <= 17; z++ {
			if x != 15 || z != 15 && z != 16 {
				adjacent = append(adjacent, domain.Cell{X: x, Z: z})
			}
		}
	}
	rooms := []policy.Room{
		{ID: "store", Role: domain.Known(policy.RoomRoleStoreroom), Cells: roomCells},
		{ID: "dining", Role: domain.Known(policy.RoomRoleDiningRoom), Cells: roomCells},
	}
	surfaces := []policy.DiningSurface{{ID: "table", RoomID: "dining", Adjacent: adjacent}}
	bounds := policy.Bounds{Width: 30, Height: 30}
	room, sites, err := mealShelfSites(rooms, surfaces, bounds, cells, nil)
	if err != nil || room != "dining" || len(sites) == 0 || len(sites[0]) != 4 {
		t.Fatal(room, sites, err)
	}
	chair := map[domain.Cell]bool{}
	for _, c := range adjacent {
		chair[c] = true
	}
	near := false
	for _, c := range sites[0] {
		if chair[c] || c.X == 15 && (c.Z == 15 || c.Z == 16) {
			t.Fatal("shelf must stay off the table and its chair cells", sites[0])
		}
		near = near || c.X >= 13 && c.X <= 17 && c.Z >= 13 && c.Z <= 18
	}
	if !near {
		t.Fatal("shelf must touch the table's chair ring", sites[0])
	}
	// No surface in a Dining room means no shelf yet.
	if room, sites, err := mealShelfSites(rooms, nil, bounds, cells, nil); err != nil || room != "" || sites != nil {
		t.Fatal(room, sites, err)
	}
	// The shelf is a Critical meals-only allow list.
	zone, err := domain.NewAllowListStockpileZone(domain.CriticalPriority, mealShelfDefinitions, sites[0])
	if err != nil || zone.Priority() != domain.CriticalPriority || len(zone.Allow()) != len(mealShelfDefinitions) {
		t.Fatal(zone, err)
	}
}
