package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The storage planner (#1765, #1769): one deterministic function from the
// colony view to the full desired set of room-bound storage sites. Planner
// output is derived state, recomputed every pass in Go memory; the standing
// zones are the only record, and MaintainStockpiles applies the diff
// (StockpileRequest.Sited). The role registry supplies each role's filter
// and priority; a site names them for the zones it creates.

// MealStore is where the meal stockpile belongs, resolved by the caller from
// the dining facts (a standing meal closet, the freezer's door into the
// dining room, or the census dining table).
type MealStore struct {
	Room   Room
	Filter domain.StockpileFilter
	// Size is the zone's cell count: the whole room, 4 or 1.
	Size int
	// Anchor and Avoid place a one-cell store: the cells nearest Anchor off
	// Avoid.
	Anchor domain.Cell
	Avoid  []domain.Cell
	// Whole makes the zone the whole room.
	Whole bool
}

// StorageRequest is the colony view the planner reads. Layout and Rooms are
// nil while the layout plan or the room census is unknown, and no site that
// needs them is planned.
type StorageRequest struct {
	Bounds    Bounds
	Cells     []SiteCell
	Protected []domain.Cell
	Layout    *LayoutPlan
	Rooms     *RoomObservation
	// Meals is nil when no meal store is wanted or its facts are unknown.
	Meals *MealStore
}

// StoragePlan is the desired storage, most important site first.
type StoragePlan struct {
	Sites []StockpileSite
}

// PlanStorage returns the desired storage sites: the meal store, the
// freezer's raw meat, raw vegetable and corpse shelves and its perishables
// catch-all, and the tomb's corpse store.
func PlanStorage(r StorageRequest) StoragePlan {
	var plan StoragePlan
	if r.Meals != nil && r.Meals.Room.ID != "" {
		plan.Sites = append(plan.Sites, r.mealSite(*r.Meals))
	}
	if r.Layout == nil || r.Rooms == nil {
		return plan
	}
	plan.Sites = append(plan.Sites, r.freezerSites()...)
	plan.Sites = append(plan.Sites, r.tombSites()...)
	return plan
}

func (r StorageRequest) mealSite(meals MealStore) StockpileSite {
	site := StockpileSite{Role: domain.MealsRolePrefix + meals.Room.ID, Room: meals.Room.Cells, Filter: meals.Filter, Priority: domain.CriticalPriority, Size: meals.Size}
	switch {
	case meals.Whole:
		site.Candidates = [][]domain.Cell{meals.Room.Cells}
	case meals.Size == 1:
		site.Candidates = roomCellSites(meals.Room.Cells, meals.Anchor, r.Cells, append(append([]domain.Cell(nil), r.Protected...), meals.Avoid...))
	default:
		site.Candidates, _ = roomStorageSites(meals.Room.Cells, meals.Anchor, r.Bounds, r.Cells, r.Protected)
	}
	return site
}

// freezerSites are dedicated 2x2 shelves nearest the kitchen door, then one
// lower priority zone over the rest of the freezer taking every perishable.
func (r StorageRequest) freezerSites() []StockpileSite {
	room, sites, err := rawFoodStockSites(*r.Layout, *r.Rooms, r.Bounds, r.Cells, r.Protected)
	if err != nil || room.ID == "" {
		return nil
	}
	var out []StockpileSite
	for _, shelf := range []struct {
		prefix string
		filter domain.StockpileFilter
	}{{domain.RawMeatRolePrefix, domain.RawMeatFilter()}, {domain.RawVegRolePrefix, domain.RawVegFilter()}, {domain.CorpsesRolePrefix, domain.CorpseLarderFilter()}} {
		candidates := sites
		if shelf.prefix == domain.CorpsesRolePrefix {
			// Carcasses stay by the butcher's door into the freezer.
			if door, ok := butcheryDoor(*r.Layout); ok {
				if near, err := roomStorageSites(room.Cells, door, r.Bounds, r.Cells, r.Protected); err == nil && len(near) > 0 {
					candidates = near
				}
			}
		}
		out = append(out, StockpileSite{Role: shelf.prefix + room.ID, Room: room.Cells, Filter: shelf.filter, Priority: domain.CriticalPriority, Candidates: candidates})
	}
	return append(out, StockpileSite{Role: domain.PerishablesRolePrefix + room.ID, Room: room.Cells, Filter: domain.PerishablesFilter(), Priority: domain.PreferredPriority, Remainder: true,
		Candidates: [][]domain.Cell{roomPool(room.Cells, r.Cells, r.Protected)}})
}

// tombSites are the first standing tomb's corpse store, the whole room.
func (r StorageRequest) tombSites() []StockpileSite {
	for _, tomb := range r.Layout.AllRooms() {
		if tomb.Role != ModuleTomb {
			continue
		}
		if room, ok := PlannedRoomStanding(tomb, *r.Rooms); ok && len(room.Cells) > 0 {
			return []StockpileSite{{Role: domain.TombRolePrefix + room.ID, Room: room.Cells, Filter: domain.TombCorpsesFilter(), Priority: domain.CriticalPriority, Remainder: true,
				Candidates: [][]domain.Cell{roomPool(room.Cells, r.Cells, r.Protected)}}}
		}
	}
	return nil
}
