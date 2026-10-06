package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The room-bound stockpile roles (#917). The meal closet, the table cell, the
// medicine store and the food store are declared by their departments from the
// layout plan (policy.foodOwner, policy.medicalOwner); the one still reading a
// built fact is the table cell (tableMeal). The raw-food role (#722) is a 2x2
// Critical stockpile of raw meat and raw plant food inside the planned freezer:
// Critical because vanilla ranks Preferred below Important, and only a rank
// above the starter Important food zone hauls raw food into the cold.
//
// All are fixed-size: MaintainStockpiles never resizes them.

// mealSpotMinPerDay is the fewest meals a day the colony eats before a
// warm spot by the table pays: fewer, and the meals wait in the store.
const mealSpotMinPerDay = 3.0

func allowOnly(definitions ...string) domain.StockpileFilter {
	f, err := domain.AllowOnlyFilter(definitions)
	if err != nil {
		panic(err)
	}
	return f
}

// tableMeal is the one-cell meal store by the dining table (#936), nil while a
// fact it needs is unknown: a spot in the planned dining room holding the
// table, while the colony eats at least mealSpotMinPerDay meals a day, else a
// retirement of the zone. The cell sits off the chairs, nearest where they are.
func tableMeal(projection *observation.ColonyProjection) *policy.MealStore {
	if projection == nil {
		return nil
	}
	plan, pk := projection.LayoutPlan.Value()
	comfort, ck := projection.Facts.Comfort.Value()
	if !pk || !ck {
		return nil
	}
	var dining []policy.PlannedRoom
	for _, planned := range plan.AllRooms() {
		if planned.Role == policy.PlannedDining {
			dining = append(dining, planned)
		}
	}
	if len(dining) == 0 {
		return nil
	}
	retired := &policy.MealStore{Dining: dining[0].Interior, Retired: true}
	var room policy.PlannedRoom
	var adjacent []domain.Cell
	for _, planned := range dining {
		for _, s := range comfort.Surfaces {
			if len(s.Adjacent) > 0 && inRectangle(planned.Interior, s.Adjacent[0]) {
				room, adjacent = planned, s.Adjacent
				break
			}
		}
		if adjacent != nil {
			break
		}
	}
	if adjacent == nil {
		return retired
	}
	benches, bk := projection.ProductionBenches.Value()
	if !bk {
		return nil
	}
	// The meal is what the active bills cook; none cooked, no warm spot.
	meal, nutrition, cooked := policy.ObservedMeal(benches)
	if !cooked {
		return retired
	}
	perDay, known := mealsPerDay(projection.FoodSupply, nutrition)
	if !known {
		return nil
	}
	if perDay < mealSpotMinPerDay {
		return retired
	}
	return &policy.MealStore{Dining: room.Interior, Filter: allowOnly(meal), Anchor: centroid(adjacent), Avoid: adjacent}
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

func inRectangle(r policy.Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height
}

func centroid(cells []domain.Cell) domain.Cell {
	var sumX, sumZ int64
	for _, cell := range cells {
		sumX += int64(cell.X)
		sumZ += int64(cell.Z)
	}
	return domain.Cell{X: int32(sumX / int64(len(cells))), Z: int32(sumZ / int64(len(cells)))}
}

// storeView is the colony view the storage planner reads: the cells,
// the layout plan and room census when known, the table meal store's spot and
// the food stockpile while the colony's food storage is not met.
func storeView(projection *observation.ColonyProjection, protected []domain.Cell) policy.StoreView {
	request := policy.StoreView{Bounds: projection.Bounds, Cells: projection.Cells, Protected: protected}
	if plan, rooms, known := plannedLayout(*projection); known {
		request.Layout, request.Rooms = &plan, &rooms
	}
	request.Shapes = projection.Shapes
	request.Meals = tableMeal(projection)
	if met, known := projection.Facts.FoodStorage.Value(); !known || !met {
		request.Food = &policy.FoodStore{}
	}
	return request
}
