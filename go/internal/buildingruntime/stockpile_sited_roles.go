package buildingruntime

import (
	"sort"

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

// mealTierDefinitions is the one meal a warm spot by the table holds.
var mealTierDefinitions = map[policy.MealTier]string{policy.MealSimple: "MealSimple", policy.MealFine: "MealFine", policy.MealLavish: "MealLavish", policy.MealPaste: "MealNutrientPaste"}

// mealSpotMinPerDay is the fewest meals a day the colony eats before a
// warm spot by the table pays: fewer, and the meals wait in the store.
const mealSpotMinPerDay = 3.0

// mealNutrition is one cooked meal's nutrition.
const mealNutrition = 0.9

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
	perDay, known := mealsPerDay(projection.FoodSupply)
	if !known {
		return mealSpot{}, false
	}
	if perDay < mealSpotMinPerDay {
		return mealSpot{}, true
	}
	benches, bk := projection.ProductionBenches.Value()
	if !bk {
		return mealSpot{}, false
	}
	meal, ok := mealTierDefinitions[policy.ObservedMealTier(benches)]
	if !ok {
		return mealSpot{}, false
	}
	return mealSpot{room: room, filter: allowOnly(meal), size: 1, anchor: centroid(adjacent), avoid: adjacent}, true
}

// coldMealSpot is the standing meal closet, else the standing planned
// freezer sharing a door with the standing planned dining room.
func coldMealSpot(plan policy.LayoutPlan, rooms policy.RoomObservation) (mealSpot, bool) {
	var dining *policy.LayoutRoom
	for i, planned := range plan.Rooms {
		switch planned.Role {
		case policy.ModuleMealCloset:
			if room, ok := policy.PlannedRoomStanding(planned, rooms); ok && len(room.Cells) > 0 {
				return mealSpot{room: room, filter: mealShelfFilter(), size: len(room.Cells), whole: true}, true
			}
		case policy.ModuleDining:
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
// over one meal's; unknown while any colonist's need is.
func mealsPerDay(supply domain.Fact[policy.FoodSupply]) (float64, bool) {
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

// stockpileSites are the room-bound roles the projection has a room for;
// none while the room census (or, for the freezer, the layout plan) is
// unknown.
func stockpileSites(projection *observation.ColonyProjection, protected []domain.Cell) []policy.StockpileSite {
	var out []policy.StockpileSite
	if spot, known := findMealSpot(projection); known && spot.room.ID != "" {
		site := policy.StockpileSite{Role: domain.MealsRolePrefix + spot.room.ID, Room: spot.room.Cells, Filter: spot.filter, Priority: domain.CriticalPriority, Size: spot.size}
		switch {
		case spot.whole:
			site.Candidates = [][]domain.Cell{spot.room.Cells}
		case spot.size == 1:
			site.Candidates = roomCellSites(spot.room.Cells, spot.anchor, projection.Cells, append(append([]domain.Cell(nil), protected...), spot.avoid...))
		default:
			site.Candidates, _ = roomStorageSites(spot.room.Cells, spot.anchor, projection.Bounds, projection.Cells, protected)
		}
		out = append(out, site)
	}
	if layout, planned, known := plannedLayout(*projection); known {
		if room, sites, err := rawFoodStockSites(layout, planned, projection.Bounds, projection.Cells, protected); err == nil && room.ID != "" {
			// Dedicated 2x2 shelves nearest the kitchen door, then one lower
			// priority zone over the rest of the freezer taking every perishable.
			for _, shelf := range []struct {
				prefix string
				filter domain.StockpileFilter
			}{{domain.RawMeatRolePrefix, domain.RawMeatFilter()}, {domain.RawVegRolePrefix, domain.RawVegFilter()}, {domain.CorpsesRolePrefix, domain.CorpseLarderFilter()}} {
				out = append(out, policy.StockpileSite{Role: shelf.prefix + room.ID, Room: room.Cells, Filter: shelf.filter, Priority: domain.CriticalPriority, Candidates: sites})
			}
			out = append(out, policy.StockpileSite{Role: domain.PerishablesRolePrefix + room.ID, Room: room.Cells, Filter: domain.PerishablesFilter(), Priority: domain.PreferredPriority, Remainder: true,
				Candidates: [][]domain.Cell{roomPool(room.Cells, projection.Cells, protected)}})
		}
	}
	if layout, planned, known := plannedLayout(*projection); known {
		for _, tomb := range layout.AllRooms() {
			if tomb.Role != policy.ModuleTomb {
				continue
			}
			if room, ok := policy.PlannedRoomStanding(tomb, planned); ok && len(room.Cells) > 0 {
				out = append(out, policy.StockpileSite{Role: domain.TombRolePrefix + room.ID, Room: room.Cells, Filter: domain.TombCorpsesFilter(), Priority: domain.CriticalPriority, Remainder: true,
					Candidates: [][]domain.Cell{roomPool(room.Cells, projection.Cells, protected)}})
				break
			}
		}
	}
	return out
}

// roomCellSiteLimit bounds the one-cell candidates listed.
const roomCellSiteLimit = 16

// roomCellSites are the free roofed single cells inside room, nearest
// anchor first, never on avoid.
func roomCellSites(room []domain.Cell, anchor domain.Cell, cells []policy.SiteCell, avoid []domain.Cell) [][]domain.Cell {
	inside := make(map[domain.Cell]bool, len(room))
	for _, c := range room {
		inside[c] = true
	}
	skip := make(map[domain.Cell]bool, len(avoid))
	for _, c := range avoid {
		skip[c] = true
	}
	var free []domain.Cell
	for _, c := range cells {
		if !inside[c.Cell] || skip[c.Cell] {
			continue
		}
		walkable, _ := c.Walkable.Value()
		roofed, _ := c.Roofed.Value()
		empty, _ := c.StorageEmpty.Value()
		occupied, ok := c.Occupied.Value()
		if walkable && roofed && empty && ok && !occupied {
			free = append(free, c.Cell)
		}
	}
	distance := func(c domain.Cell) int64 {
		dx, dz := int64(c.X-anchor.X), int64(c.Z-anchor.Z)
		return dx*dx + dz*dz
	}
	sort.Slice(free, func(i, j int) bool {
		di, dj := distance(free[i]), distance(free[j])
		if di != dj {
			return di < dj
		}
		return free[i].Z < free[j].Z || free[i].Z == free[j].Z && free[i].X < free[j].X
	})
	out := make([][]domain.Cell, 0, min(len(free), roomCellSiteLimit))
	for _, c := range free[:min(len(free), roomCellSiteLimit)] {
		out = append(out, []domain.Cell{c})
	}
	return out
}

// rawFoodStockSites finds the first planned freezer standing in the census
// and lists the 2x2 patches inside it nearest its door into the kitchen (the
// Link; the outer Door on plans saved before #819). A zero room means no
// planned freezer stands yet.
func rawFoodStockSites(layout policy.LayoutPlan, rooms policy.RoomObservation, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (policy.Room, [][]domain.Cell, error) {
	for _, planned := range layout.AllRooms() {
		if planned.Role != policy.ModuleFreezer {
			continue
		}
		room, ok := policy.PlannedRoomStanding(planned, rooms)
		if !ok || len(room.Cells) == 0 {
			continue
		}
		anchor := planned.Door
		if planned.Link != nil {
			anchor = *planned.Link
		}
		sites, err := roomStorageSites(room.Cells, anchor, bounds, cells, protected)
		return room, sites, err
	}
	return policy.Room{}, nil, nil
}

func centroid(cells []domain.Cell) domain.Cell {
	var sumX, sumZ int64
	for _, cell := range cells {
		sumX += int64(cell.X)
		sumZ += int64(cell.Z)
	}
	return domain.Cell{X: int32(sumX / int64(len(cells))), Z: int32(sumZ / int64(len(cells)))}
}

// roomStorageSites is the bounded list of free roofed 2x2 patches inside
// room, nearest anchor first; nil when nothing fits.
func roomStorageSites(room []domain.Cell, anchor domain.Cell, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) ([][]domain.Cell, error) {
	inside := make(map[domain.Cell]bool, len(room))
	for _, cell := range room {
		inside[cell] = true
	}
	var scoped []policy.SiteCell
	for _, cell := range cells {
		if inside[cell.Cell] {
			scoped = append(scoped, cell)
		}
	}
	if len(scoped) == 0 {
		return nil, nil
	}
	sites, err := policy.CoveredStorageSites(policy.CoveredStorageRequest{Bounds: bounds, Anchor: anchor, Cells: scoped, Protected: protected})
	if err != nil || len(sites) == 0 {
		return nil, err
	}
	out := make([][]domain.Cell, 0, len(sites))
	for _, site := range sites {
		var block []domain.Cell
		for x := site.X; x < site.X+site.Width; x++ {
			for z := site.Z; z < site.Z+site.Height; z++ {
				block = append(block, domain.Cell{X: x, Z: z})
			}
		}
		out = append(out, block)
	}
	return out, nil
}

// stockpileGearRooms are the standing planned rooms the gear stockpiles
// belong in: apparel in the storage room, weapons in the barracks when one
// stands, else the storage room.
func stockpileGearRooms(projection *observation.ColonyProjection) map[string][]domain.Cell {
	plan, rooms, known := plannedLayout(*projection)
	if !known {
		return nil
	}
	standing := func(role policy.ModuleRole) []domain.Cell {
		for _, planned := range plan.AllRooms() {
			if planned.Role != role {
				continue
			}
			if room, ok := policy.PlannedRoomStanding(planned, rooms); ok && len(room.Cells) > 0 {
				return room.Cells
			}
		}
		return nil
	}
	storage := standing(policy.ModuleStorage)
	weapons := standing(policy.ModuleBarracks)
	if weapons == nil {
		weapons = storage
	}
	return map[string][]domain.Cell{domain.ApparelRole: storage, domain.WeaponsRole: weapons}
}

// roomPool is the free roofed walkable cells inside room, never on avoid:
// what the freezer's catch-all zone may cover.
func roomPool(room []domain.Cell, cells []policy.SiteCell, avoid []domain.Cell) []domain.Cell {
	inside := make(map[domain.Cell]bool, len(room))
	for _, c := range room {
		inside[c] = true
	}
	skip := make(map[domain.Cell]bool, len(avoid))
	for _, c := range avoid {
		skip[c] = true
	}
	var out []domain.Cell
	for _, c := range cells {
		if !inside[c.Cell] || skip[c.Cell] {
			continue
		}
		walkable, _ := c.Walkable.Value()
		roofed, _ := c.Roofed.Value()
		empty, _ := c.StorageEmpty.Value()
		occupied, ok := c.Occupied.Value()
		if walkable && roofed && empty && ok && !occupied {
			out = append(out, c.Cell)
		}
	}
	return out
}
