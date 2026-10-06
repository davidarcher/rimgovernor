package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// foodOwner is the Food department's stockpiles (#2193, #2219): the meal
// closet, the table cell, the freezer's shelves and perishables catch-all, and
// the food store. Each is declared once and sited from the layout plan at plan
// time, at its real priority, sized once; only the table cell reads a built
// fact (the table).
type foodOwner struct{}

func (foodOwner) Department() Department { return DepartmentFood }

func (o foodOwner) RoomDemand(v StoreView) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}

// Stores are, most important first: the meal store, the freezer's Critical
// shelves nearest the kitchen door (raw meat, raw vegetables, animal and
// insect corpses by the butcher's door), the perishables catch-all over the
// rest of the freezer, then the food store.
func (foodOwner) Stores(v StoreView) []Store {
	var out []Store
	if v.Layout != nil {
		out = append(out, v.mealClosets()...)
	}
	if v.Meals != nil {
		out = append(out, v.tableStore(*v.Meals))
	}
	if v.Layout != nil {
		out = append(out, v.freezerMealShelf()...)
		out = append(out, v.freezerStores()...)
	}
	if food, ok := v.foodStore(); ok {
		out = append(out, food)
	}
	return out
}

// plannedKey names a planned room's zone role by its interior, the identity
// the zone is matched to.
func plannedKey(prefix string, interior Rectangle) string {
	return fmt.Sprintf("%s%d_%d", prefix, interior.X, interior.Z)
}

// mealClosets are the planned meal closets, each covered whole.
func (r StoreView) mealClosets() []Store {
	var out []Store
	for _, closet := range r.Layout.AllRooms() {
		if closet.Role == PlannedMealCloset {
			out = append(out, Store{StoreSite: StoreSite{Role: plannedKey(domain.MealsRolePrefix, closet.Interior), Interior: closet.Interior,
				Filter: domain.MealShelfFilter(), Priority: domain.CriticalPriority}})
		}
	}
	return out
}

// tableStore is the one cell of the cooked meal by the dining table, off the
// chairs. It is the one store sited from a built fact (the table); its role is
// exact so retiring it never touches the closet or the freezer shelf.
func (r StoreView) tableStore(meals MealStore) Store {
	return Store{Retired: meals.Retired, StoreSite: StoreSite{Role: plannedKey(domain.MealsRolePrefix, meals.Dining), Interior: meals.Dining, Width: 1, Height: 1,
		Anchor: meals.Anchor, Avoid: meals.Avoid, Filter: meals.Filter, Priority: domain.CriticalPriority, exact: true}}
}

func withoutCells(cells, drop []domain.Cell) []domain.Cell {
	skip := cellSet(drop)
	out := make([]domain.Cell, 0, len(cells))
	for _, c := range cells {
		if !skip[c] {
			out = append(out, c)
		}
	}
	return out
}

// freezer is the first planned freezer.
func (r StoreView) freezer() (PlannedRoom, bool) {
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == PlannedFreezer {
			return planned, true
		}
	}
	return PlannedRoom{}, false
}

// freezerMealShelf is the 2x2 meal shelf in the freezer at its door into the
// dining room, planned with the freezer so the perishables catch-all never
// takes its ground.
func (r StoreView) freezerMealShelf() []Store {
	for _, dining := range r.Layout.Rooms {
		if dining.Role != PlannedDining {
			continue
		}
		if freezer, door, ok := r.Layout.FreezerDoorInto(dining); ok {
			return []Store{{StoreSite: StoreSite{Role: plannedKey(domain.MealsRolePrefix, freezer.Interior), Interior: freezer.Interior, Width: 2, Height: 2, Anchor: door,
				Filter: domain.MealShelfFilter(), Priority: domain.CriticalPriority}}}
		}
		return nil
	}
	return nil
}

// freezerStores are dedicated 2x2 shelves nearest the kitchen door, then one
// lower priority zone over the rest of the freezer taking every perishable.
func (r StoreView) freezerStores() []Store {
	freezer, ok := r.freezer()
	if !ok {
		return nil
	}
	anchor := freezer.Door
	if freezer.Link != nil {
		anchor = *freezer.Link
	}
	var out []Store
	for _, shelf := range []struct {
		prefix string
		filter domain.StockpileFilter
	}{{domain.RawMeatRolePrefix, domain.RawMeatFilter()}, {domain.RawVegRolePrefix, domain.RawVegFilter()}, {domain.CorpsesRolePrefix, domain.CorpseLarderFilter()}} {
		at := anchor
		if shelf.prefix == domain.CorpsesRolePrefix {
			// Carcasses stay by the butcher's door into the freezer.
			if door, ok := butcheryDoor(*r.Layout); ok {
				at = door
			}
		}
		out = append(out, Store{StoreSite: StoreSite{Role: plannedKey(shelf.prefix, freezer.Interior), Interior: freezer.Interior, Width: 2, Height: 2, Anchor: at,
			Filter: shelf.filter, Priority: domain.CriticalPriority}})
	}
	return append(out, Store{StoreSite: StoreSite{Role: plannedKey(domain.PerishablesRolePrefix, freezer.Interior), Interior: freezer.Interior,
		Filter: domain.PerishablesFilter(), Priority: domain.PreferredPriority}})
}
