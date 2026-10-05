package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The room-bound stockpile roles (#917), created by MaintainStockpiles
// whenever their room stands without one, whatever other goal is in
// deficit:
//
//   - meals:<roomID>, the meal stockpile (#872, #936), where cooked meals
//     keep near the table (findMealSpot): the standing meal closet, whole;
//     else a 2x2 in the standing freezer at its door into the dining room;
//     else, while the colony eats at least mealSpotMinPerDay meals a day,
//     one cell of the one meal it cooks beside the census dining table, off
//     the chairs, so few meals sit warm. With no spot the role retires.
//   - rawfood:<roomID>, the raw-food stock (#722): a 2x2 Critical stockpile
//     of raw meat and raw plant food inside the planned freezer, nearest its
//     door into the kitchen, so cooks fetch cold ingredients a step from the
//     stove. Critical because vanilla ranks Preferred below Important, and
//     only a rank above the starter Important food zone hauls raw food into
//     the cold.
//
// Both are fixed-size: MaintainStockpiles never grows or merges them, and
// shrinks a meal zone only to its spot's size.

// mealShelfDefinitions are the cooked meals a cold meal spot accepts.
var mealShelfDefinitions = []string{"MealFine", "MealLavish", "MealNutrientPaste", "MealSimple", "MealSurvivalPack"}

// mealSpotMinPerDay is the fewest meals a day the colony eats before a
// warm spot by the table pays: fewer, and the meals wait in the store.
const mealSpotMinPerDay = 3.0

func mealShelfFilter() domain.StockpileFilter {
	return allowOnly(mealShelfDefinitions...)
}

func allowOnly(definitions ...string) domain.StockpileFilter {
	f, err := domain.AllowOnlyFilter(definitions)
	if err != nil {
		panic(err)
	}
	return f
}

func init() {
	rawFood := policy.StockpileRoleState{Filter: domain.RawFoodFilter(), Priority: domain.CriticalPriority, Fixed: true}
	RegisterStockpileRole("meals", func(in StockpileRoleInput, _ string) (policy.StockpileRoleState, bool) {
		spot, known := findMealSpot(in.Projection)
		if !known {
			return policy.StockpileRoleState{}, false
		}
		if spot.room.ID == "" {
			return policy.StockpileRoleState{Retired: true}, true
		}
		return policy.StockpileRoleState{Filter: spot.filter, Priority: domain.CriticalPriority, Fixed: true}, true
	})
	RegisterStockpileRole(domain.YardRole, fixedStockpileRole(domain.YardFilter(), domain.LowPriority))
	RegisterStockpileRole("rawfood", func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) { return rawFood, true })
}

// mealSpot is where the meal stockpile belongs: the room, the meals it
// holds, its size, and where its cells sit (the whole room, or the cells
// nearest anchor off avoid).
type mealSpot struct {
	room   policy.Room
	filter domain.StockpileFilter
	size   int
	anchor domain.Cell
	avoid  []domain.Cell
	whole  bool
}

// findMealSpot is the meal stockpile's spot (#936), best first: the
// standing meal closet; the standing freezer with a door into the standing
// dining room; one cell by the census dining table while the colony eats
// at least mealSpotMinPerDay meals a day. A zero room is no spot; known is
// false while a fact the choice needs is unknown.
func findMealSpot(projection *observation.ColonyProjection) (mealSpot, bool) {
	if projection == nil {
		return mealSpot{}, false
	}
	rooms, rk := projection.Rooms.Value()
	if !rk {
		return mealSpot{}, false
	}
	if plan, pk := projection.LayoutPlan.Value(); pk {
		if spot, ok := coldMealSpot(plan, rooms); ok {
			return spot, true
		}
	}
	comfort, ck := projection.Facts.Comfort.Value()
	if !ck {
		return mealSpot{}, false
	}
	room, adjacent := diningTable(rooms.Rooms, comfort.Surfaces)
	if room.ID == "" {
		return mealSpot{}, true
	}
	benches, bk := projection.ProductionBenches.Value()
	if !bk {
		return mealSpot{}, false
	}
	// The meal is what the active bills cook; none cooked, no warm spot.
	meal, nutrition, cooked := policy.ObservedMeal(benches)
	if !cooked {
		return mealSpot{}, true
	}
	perDay, known := mealsPerDay(projection.FoodSupply, nutrition)
	if !known {
		return mealSpot{}, false
	}
	if perDay < mealSpotMinPerDay {
		return mealSpot{}, true
	}
	return mealSpot{room: room, filter: allowOnly(meal), size: 1, anchor: centroid(adjacent), avoid: adjacent}, true
}

// coldMealSpot is the standing meal closet, else the standing planned
// freezer sharing a door with the standing planned dining room.
func coldMealSpot(plan policy.LayoutPlan, rooms policy.RoomObservation) (mealSpot, bool) {
	var dining *policy.PlannedRoom
	for i, planned := range plan.Rooms {
		switch planned.Role {
		case policy.PlannedMealCloset:
			if room, ok := policy.PlannedRoomStanding(planned, rooms); ok && len(room.Cells) > 0 {
				return mealSpot{room: room, filter: mealShelfFilter(), size: len(room.Cells), whole: true}, true
			}
		case policy.PlannedDining:
			if dining == nil {
				dining = &plan.Rooms[i]
			}
		}
	}
	if dining == nil {
		return mealSpot{}, false
	}
	if _, ok := policy.PlannedRoomStanding(*dining, rooms); !ok {
		return mealSpot{}, false
	}
	freezer, door, ok := plan.FreezerDoorInto(*dining)
	if !ok {
		return mealSpot{}, false
	}
	room, ok := policy.PlannedRoomStanding(freezer, rooms)
	if !ok || len(room.Cells) == 0 {
		return mealSpot{}, false
	}
	return mealSpot{room: room, filter: mealShelfFilter(), size: 4, anchor: door}, true
}

// mealsPerDay is the meals the colonists eat a day: their nutrition need
// over one meal's (the cooked meal's nutrition); unknown while any colonist's need is.
func mealsPerDay(supply domain.Fact[policy.FoodSupply], mealNutrition float64) (float64, bool) {
	s, known := supply.Value()
	if !known {
		return 0, false
	}
	total := 0.0
	for _, c := range s.Consumers {
		need, nk := c.NutritionPerDay.Value()
		if !nk {
			return 0, false
		}
		total += need
	}
	return total / mealNutrition, true
}

// diningTable is the first census Dining room holding a dining surface and
// that surface's adjacent cells (the chairs' places); a zero room when no
// table stands in a Dining room.
func diningTable(rooms []policy.Room, surfaces []policy.DiningSurface) (policy.Room, []domain.Cell) {
	for _, room := range rooms {
		role, known := room.Role.Value()
		if !known || role != policy.RoomRoleDiningRoom || len(room.Cells) == 0 {
			continue
		}
		for _, s := range surfaces {
			if s.RoomID == room.ID && len(s.Adjacent) > 0 {
				return room, s.Adjacent
			}
		}
	}
	return policy.Room{}, nil
}

func centroid(cells []domain.Cell) domain.Cell {
	var sumX, sumZ int64
	for _, cell := range cells {
		sumX += int64(cell.X)
		sumZ += int64(cell.Z)
	}
	return domain.Cell{X: int32(sumX / int64(len(cells))), Z: int32(sumZ / int64(len(cells)))}
}

// storageRequest is the colony view the storage planner reads: the cells,
// the layout plan and room census when known, the meal store's spot and the
// food stockpile while the colony's food storage is not met.
func storageRequest(projection *observation.ColonyProjection, protected []domain.Cell) policy.StorageRequest {
	request := policy.StorageRequest{Bounds: projection.Bounds, Cells: projection.Cells, Protected: protected}
	if plan, rooms, known := plannedLayout(*projection); known {
		request.Layout, request.Rooms = &plan, &rooms
	}
	if sleeping, known := projection.Facts.Sleeping.Value(); known {
		request.Sleeping = &sleeping
	}
	if spot, known := findMealSpot(projection); known && spot.room.ID != "" {
		request.Meals = &policy.MealStore{Room: spot.room, Filter: spot.filter, Size: spot.size, Anchor: spot.anchor, Avoid: spot.avoid, Whole: spot.whole}
	}
	if met, known := projection.Facts.FoodStorage.Value(); !known || !met {
		anchor, cooking := cookingSpot(projection)
		core, planned := planCore(*projection)
		if !cooking {
			anchor = core
		}
		if cooking || planned {
			request.Food = &policy.FoodStore{Anchor: anchor}
		}
	}
	return request
}

// cookingBenchDefinitions are the cooking benches the food stockpile sits
// beside.
var cookingBenchDefinitions = map[string]bool{"Campfire": true, "FueledStove": true, "ElectricStove": true}

// cookingSpot is the first claimed or built cooking bench's cell.
func cookingSpot(projection *observation.ColonyProjection) (domain.Cell, bool) {
	if census, ok := projection.Facts.CurrentConstruction.Value(); ok {
		for _, b := range census.Buildings {
			if cookingBenchDefinitions[b.Building.Definition()] {
				return b.Building.Cell(), true
			}
		}
	}
	return domain.Cell{}, false
}
