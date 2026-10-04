package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// mealSpotColony is a recorded 30x30 roofed colony: census dining room
// Room_4 over (10..19, 10..19) with a 1x2 table at (15,15)-(15,16), and
// colonists eating need nutrition a day each.
func mealSpotColony(needs ...float64) (*observation.ColonyProjection, []domain.Cell) {
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 30, Height: 30}, Facts: policy.RoundsFacts{Colonists: domain.Known(int64(len(needs)))}}
	projection.Identity.Tick = 9000
	var roomCells, adjacent []domain.Cell
	for x := int32(0); x < 30; x++ {
		for z := int32(0); z < 30; z++ {
			table := x == 15 && (z == 15 || z == 16)
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(true), Indoors: domain.Known(true), Walkable: domain.Known(!table), Occupied: domain.Known(table), Zone: domain.Known(false), ZoneID: domain.Known(""), StorageEmpty: domain.Known(true)}
			if x >= 10 && x < 20 && z >= 10 && z < 20 {
				roomCells = append(roomCells, cell.Cell)
			}
			if x >= 14 && x <= 16 && z >= 14 && z <= 17 && !table {
				adjacent = append(adjacent, cell.Cell)
			}
			projection.Cells = append(projection.Cells, cell)
		}
	}
	projection.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{{ID: "Room_4", Role: domain.Known(policy.RoomRoleDiningRoom), Enclosed: domain.Known(true), Cells: roomCells}}})
	projection.Facts.Comfort = domain.Known(policy.ComfortObservation{Surfaces: []policy.DiningSurface{{ID: "Table_1", RoomID: "Room_4", Adjacent: adjacent}}})
	projection.Facts.FoodStorage = domain.Known(true)
	var consumers []policy.FoodConsumer
	for i, need := range needs {
		consumers = append(consumers, policy.FoodConsumer{ID: policy.PawnID(rune('a' + i)), NutritionPerDay: domain.Known(need)})
	}
	projection.FoodSupply = domain.Known(policy.FoodSupply{Consumers: consumers})
	// The colony cooks the simple meal (0.9 nutrition) on one bill.
	cook := policy.ProductionBench{
		ID: "Stove_1", Definition: "ElectricStove",
		Recipes: []policy.ProductionRecipe{{Name: "CookMealSimple", Mood: domain.Known(0.0), Products: []policy.ProductionProduct{{Name: "MealSimple", Nutrition: domain.Known(0.9), Edible: domain.Known(true)}}}},
		Bills:   []policy.ExistingProductionBill{{ID: "Bill_1", Recipe: "CookMealSimple", Active: domain.Known(true)}},
	}
	projection.ProductionBenches = domain.Known([]policy.ProductionBench{cook})
	return projection, adjacent
}

// zoneOn marks cells as zone id, stocked or not.
func zoneOn(projection *observation.ColonyProjection, id string, stocked bool, cells ...domain.Cell) {
	on := map[domain.Cell]bool{}
	for _, c := range cells {
		on[c] = true
	}
	for i := range projection.Cells {
		if on[projection.Cells[i].Cell] {
			projection.Cells[i].Zone, projection.Cells[i].ZoneID, projection.Cells[i].StorageEmpty = domain.Known(true), domain.Known(id), domain.Known(!stocked)
		}
	}
}

// A snapshot over recorded colony facts (#936): with no cold spot and the
// colony eating 3+ meals a day, the meal stockpile is one cell of the one
// meal it cooks beside the table, off the chairs. A 2x2 meal shelf of
// the role is retargeted to that meal, then shrunk to one cell;
// under 3 meals a day the role retires and the zone goes.
func TestMealSpotByTheTableIsOneCellOfOneMeal(t *testing.T) {
	t.Parallel()
	projection, adjacent := mealSpotColony(1.6, 1.6)
	simple := allowOnly("MealSimple")
	review := policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(projection, nil, nil, domain.Unknown[map[string]bool](), nil, nil, nil)))
	if len(review.Edits) != 1 {
		t.Fatalf("review %+v", review)
	}
	create := review.Edits[0]
	if create.Kind != policy.StockpileCreate || create.Role != "meals:Room_4" || create.Priority != domain.CriticalPriority || create.Filter != simple || len(create.Cells) != 1 {
		t.Fatalf("create %+v", create)
	}
	c := create.Cells[0]
	for _, a := range adjacent {
		if a == c {
			t.Fatal("meal cell on a chair", c)
		}
	}
	if c.X < 13 || c.X > 17 || c.Z < 13 || c.Z > 18 {
		t.Fatal("meal cell far from the table", c)
	}

	shelf := []domain.Cell{{X: 11, Z: 11}, {X: 11, Z: 12}, {X: 12, Z: 11}, {X: 12, Z: 12}}
	zoneOn(projection, "Zone_7", false, shelf...)
	zoneOn(projection, "Zone_7", true, shelf[0])
	owned := []store.OwnedZone{{ID: "Zone_7", Kind: domain.StockpileZone, Role: "meals:Room_4", Filter: mealShelfFilter(), Priority: domain.CriticalPriority}}
	review = policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(projection, owned, nil, domain.Unknown[map[string]bool](), nil, nil, nil)))
	if len(review.Edits) != 1 || review.Edits[0].Kind != policy.StockpileRetarget || review.Edits[0].Filter != simple || review.Edits[0].Zone != "Zone_7" {
		t.Fatalf("shelf not retargeted: %+v", review)
	}
	patches := map[string]store.AppliedStockpile{"Zone_7": {Target: "Zone_7", Kind: domain.StorageZoneTarget, Filter: simple, Priority: domain.CriticalPriority, Role: "meals:Room_4"}}
	review = policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(projection, owned, patches, domain.Unknown[map[string]bool](), nil, nil, nil)))
	if len(review.Edits) != 1 || review.Edits[0].Kind != policy.StockpileShrink || len(review.Edits[0].Cells) != 3 {
		t.Fatalf("shelf not shrunk to one cell: %+v", review)
	}
	for _, removed := range review.Edits[0].Cells {
		if removed == shelf[0] {
			t.Fatal("shrink dropped the stocked cell")
		}
	}

	few, _ := mealSpotColony(1.6)
	zoneOn(few, "Zone_7", false, shelf...)
	review = policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(few, owned, patches, domain.Unknown[map[string]bool](), nil, nil, nil)))
	if len(review.Edits) != 1 || review.Edits[0].Kind != policy.StockpileDelete || review.Edits[0].Zone != "Zone_7" {
		t.Fatalf("under 3 meals a day the shelf stays: %+v", review)
	}
	few.FoodSupply = domain.Unknown[policy.FoodSupply]()
	if review = policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(few, owned, patches, domain.Unknown[map[string]bool](), nil, nil, nil))); review.Active {
		t.Fatalf("unknown demand edited: %+v", review)
	}
}

// Once the meal closet stands (#936), the meal stockpile moves into it:
// the zone by the table is deleted and the closet zoned whole for every
// meal, however few the colony eats.
func TestMealSpotMovesIntoTheStandingCloset(t *testing.T) {
	t.Parallel()
	projection, _ := mealSpotColony(1.6)
	dining := policy.LayoutRoom{Role: policy.ModuleDining, Interior: policy.Rectangle{X: 10, Z: 10, Width: 10, Height: 10}, Door: domain.Cell{X: 15, Z: 9}, DoorRot: domain.North}
	closet := policy.LayoutRoom{Role: policy.ModuleMealCloset, Interior: policy.Rectangle{X: 14, Z: 21, Width: 2, Height: 2}, Door: domain.Cell{X: 15, Z: 20}, DoorRot: domain.North}
	projection.LayoutPlan = domain.Known(policy.LayoutPlan{Rooms: []policy.LayoutRoom{dining, closet}})
	rooms, _ := projection.Rooms.Value()
	closetCells := []domain.Cell{{X: 14, Z: 21}, {X: 14, Z: 22}, {X: 15, Z: 21}, {X: 15, Z: 22}}
	rooms.Rooms = append(rooms.Rooms, policy.Room{ID: "Room_9", Enclosed: domain.Known(true), Cells: closetCells})
	projection.Rooms = domain.Known(rooms)
	zoneOn(projection, "Zone_7", true, domain.Cell{X: 13, Z: 15})
	owned := []store.OwnedZone{{ID: "Zone_7", Kind: domain.StockpileZone, Role: "meals:Room_4", Filter: allowOnly("MealSimple"), Priority: domain.CriticalPriority}}
	review := policy.PlanStockpileMaintenance(withoutOpening(stockpileRequest(projection, owned, nil, domain.Unknown[map[string]bool](), nil, nil, nil)))
	var deleted, created bool
	for _, e := range review.Edits {
		switch {
		case e.Kind == policy.StockpileDelete && e.Zone == "Zone_7":
			deleted = true
		case e.Kind == policy.StockpileCreate && e.Role == "meals:Room_9" && e.Filter == mealShelfFilter() && len(e.Cells) == 4:
			created = true
		default:
			t.Fatalf("unexpected edit %+v", e)
		}
	}
	if !deleted || !created {
		t.Fatalf("review %+v", review)
	}
}

// A standing freezer with a door into the standing dining room is the cold
// spot: a 2x2 of every meal at that door.
func TestMealSpotAtTheFreezerDoor(t *testing.T) {
	t.Parallel()
	link := domain.Cell{X: 15, Z: 12}
	freezer := policy.LayoutRoom{Role: policy.ModuleFreezer, Interior: policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 9}, DoorRot: domain.North}
	dining := policy.LayoutRoom{Role: policy.ModuleDining, Interior: policy.Rectangle{X: 16, Z: 10, Width: 9, Height: 7}, Door: domain.Cell{X: 20, Z: 9}, DoorRot: domain.North, Link: &link}
	plan := policy.LayoutPlan{Rooms: []policy.LayoutRoom{freezer, dining}}
	standing := func(id string, r policy.Rectangle) policy.Room {
		var cells []domain.Cell
		for x := r.X; x < r.X+r.Width; x++ {
			for z := r.Z; z < r.Z+r.Height; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		return policy.Room{ID: id, Enclosed: domain.Known(true), Cells: cells}
	}
	rooms := policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{standing("Room_2", freezer.Interior), standing("Room_3", dining.Interior)}}
	spot, ok := coldMealSpot(plan, rooms)
	if !ok || spot.room.ID != "Room_2" || spot.size != 4 || spot.anchor != link || spot.filter != mealShelfFilter() {
		t.Fatalf("spot %+v %v", spot, ok)
	}
	if _, ok := coldMealSpot(plan, policy.RoomObservation{Shapes: testPieceShapes, Rooms: rooms.Rooms[:1]}); ok {
		t.Fatal("cold spot without a standing dining room")
	}
}

// withoutOpening drops the opening stockpiles so a test sees only the
// sited roles' edits.
func withoutOpening(r policy.StockpileRequest) policy.StockpileRequest {
	r.Opening = false
	return r
}

// The food stockpile is planned while the colony's food storage is unmet or
// unknown, anchored at the colony core with no cooking bench, and not at all
// once the fact reads met.
func TestStorageRequestPlansFoodUntilStorageIsMet(t *testing.T) {
	t.Parallel()
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 20, Height: 20}, Center: domain.Cell{X: 7, Z: 8}}
	for _, fact := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
		projection.Facts.FoodStorage = fact
		food := storageRequest(projection, nil).Food
		if food == nil || food.Anchor != (domain.Cell{X: 7, Z: 8}) {
			t.Fatalf("food %+v", food)
		}
	}
	projection.Facts.FoodStorage = domain.Known(true)
	if food := storageRequest(projection, nil).Food; food != nil {
		t.Fatalf("food planned with storage met: %+v", food)
	}
}
