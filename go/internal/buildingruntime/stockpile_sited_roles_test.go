package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
	if err != nil || room.ID != "dining" || len(sites) == 0 || len(sites[0]) != 4 {
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
	if room, sites, err := mealShelfSites(rooms, nil, bounds, cells, nil); err != nil || room.ID != "" || sites != nil {
		t.Fatal(room, sites, err)
	}
}

// A snapshot over recorded colony facts (#917): a dining room with a table
// and no meal shelf is a MaintainStockpiles deficit of its own, a Critical
// meals-only zone with role meals:<roomID> created at the table. A zone
// serving the room (by cells, since RimWorld renumbers rooms; a role-less
// legacy shelf counts) is no deficit, and a full one never grows.
func TestMealShelfAbsenceIsAStockpileDeficit(t *testing.T) {
	t.Parallel()
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 30, Height: 30}, Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3))}}
	projection.Identity.Tick = 9000
	var roomCells, adjacent []domain.Cell
	for x := int32(0); x < 30; x++ {
		for z := int32(0); z < 30; z++ {
			table := x == 15 && (z == 15 || z == 16)
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(true), Indoors: domain.Known(true), Walkable: domain.Known(!table), Occupied: domain.Known(table), Zone: domain.Known(false), StorageEmpty: domain.Known(true)}
			if x >= 10 && x < 20 && z >= 10 && z < 20 {
				roomCells = append(roomCells, cell.Cell)
			}
			if x >= 14 && x <= 16 && z >= 14 && z <= 17 && !table {
				adjacent = append(adjacent, cell.Cell)
			}
			projection.Cells = append(projection.Cells, cell)
		}
	}
	projection.Rooms = domain.Known(policy.RoomObservation{Rooms: []policy.Room{{ID: "Room_4", Role: domain.Known(policy.RoomRoleDiningRoom), Enclosed: domain.Known(true), Cells: roomCells}}})
	projection.Facts.Comfort = domain.Known(policy.ComfortObservation{Surfaces: []policy.DiningSurface{{ID: "Table_1", RoomID: "Room_4", Adjacent: adjacent}}})

	review := policy.PlanStockpileMaintenance(stockpileRequest(projection, nil, nil, domain.Unknown[map[string]bool]()))
	if !review.Active || len(review.Edits) != 1 {
		t.Fatalf("review %+v", review)
	}
	create := review.Edits[0]
	if create.Kind != policy.StockpileCreate || create.Role != "meals:Room_4" || create.Priority != domain.CriticalPriority || create.Filter != mealShelfFilter() || len(create.Cells) != 4 {
		t.Fatalf("create %+v", create)
	}

	// The shelf stands, full, under a renumbered room: served, never grown.
	for i := range projection.Cells {
		for _, c := range create.Cells {
			if projection.Cells[i].Cell == c {
				projection.Cells[i].Zone, projection.Cells[i].ZoneID, projection.Cells[i].StorageEmpty = domain.Known(true), domain.Known("Zone_7"), domain.Known(false)
			}
		}
	}
	for _, owned := range []store.OwnedZone{
		{ID: "Zone_7", Kind: domain.StockpileZone, Role: "meals:Room_2", Filter: mealShelfFilter(), Priority: domain.CriticalPriority},
		{ID: "Zone_7", Kind: domain.StockpileZone, Filter: mealShelfFilter(), Priority: domain.CriticalPriority},
	} {
		review = policy.PlanStockpileMaintenance(stockpileRequest(projection, []store.OwnedZone{owned}, nil, domain.Unknown[map[string]bool]()))
		for _, e := range review.Edits {
			if e.Kind == policy.StockpileCreate || owned.Role != "" && e.Kind == policy.StockpileGrow {
				t.Fatalf("role %q: %+v", owned.Role, review)
			}
		}
	}
}

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
	if err != nil || room.ID != "freezer" || len(sites) == 0 || len(sites[0]) != 4 {
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
	if room, none, err := rawFoodStockSites(layout, policy.RoomObservation{}, bounds, cells, nil); err != nil || room.ID != "" || none != nil {
		t.Fatal(room, none, err)
	}
}
