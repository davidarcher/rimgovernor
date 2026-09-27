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
//   - meals:<roomID>, the meal shelf (#872): a 2x2 Critical allow-list of
//     cooked meals on the free roofed patch nearest the dining table inside
//     the census Dining room, off the table's adjacent cells (the chairs'),
//     so a hungry pawn eats at the table without walking to the storeroom.
//   - rawfood:<roomID>, the raw-food stock (#722): a 2x2 Critical stockpile
//     of raw meat and raw plant food inside the planned freezer, nearest its
//     door into the kitchen, so cooks fetch cold ingredients a step from the
//     stove. Critical because vanilla ranks Preferred below Important, and
//     only a rank above the starter Important food zone hauls raw food into
//     the cold.
//
// Both are fixed-size: MaintainStockpiles never grows, shrinks or merges
// them.

// mealShelfDefinitions are the cooked meals the shelf accepts.
var mealShelfDefinitions = []string{"MealFine", "MealLavish", "MealNutrientPaste", "MealSimple", "MealSurvivalPack"}

func mealShelfFilter() domain.StockpileFilter {
	f, err := domain.AllowOnlyFilter(mealShelfDefinitions)
	if err != nil {
		panic(err)
	}
	return f
}

func init() {
	meals := policy.StockpileRoleState{Filter: mealShelfFilter(), Priority: domain.CriticalPriority, Fixed: true}
	rawFood := policy.StockpileRoleState{Filter: domain.RawFoodFilter(), Priority: domain.CriticalPriority, Fixed: true}
	RegisterStockpileRole("meals", func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) { return meals, true })
	RegisterStockpileRole("rawfood", func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) { return rawFood, true })
}

// stockpileSites are the room-bound roles the projection has a room for;
// none while the room census (or, for the freezer, the layout plan) is
// unknown.
func stockpileSites(projection *observation.ColonyProjection, protected []domain.Cell) []policy.StockpileSite {
	var out []policy.StockpileSite
	rooms, rk := projection.Rooms.Value()
	comfort, ck := projection.Facts.Comfort.Value()
	if rk && ck {
		if room, sites, err := mealShelfSites(rooms.Rooms, comfort.Surfaces, projection.Bounds, projection.Cells, protected); err == nil && room.ID != "" {
			out = append(out, policy.StockpileSite{Role: domain.MealsRolePrefix + room.ID, Room: room.Cells, Filter: mealShelfFilter(), Priority: domain.CriticalPriority, Candidates: sites})
		}
	}
	if layout, planned, known := plannedLayout(*projection); known {
		if room, sites, err := rawFoodStockSites(layout, planned, projection.Bounds, projection.Cells, protected); err == nil && room.ID != "" {
			out = append(out, policy.StockpileSite{Role: domain.RawFoodRolePrefix + room.ID, Room: room.Cells, Filter: domain.RawFoodFilter(), Priority: domain.CriticalPriority, Candidates: sites})
		}
	}
	return out
}

// mealShelfSites finds the first census Dining room holding a dining surface
// and lists the 2x2 patches inside it nearest that table, never on the
// table's adjacent cells (the chairs' places). A zero room means no dining
// table stands in a Dining room yet.
func mealShelfSites(rooms []policy.Room, surfaces []policy.DiningSurface, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (policy.Room, [][]domain.Cell, error) {
	for _, room := range rooms {
		role, known := room.Role.Value()
		if !known || role != policy.RoomRoleDiningRoom || len(room.Cells) == 0 {
			continue
		}
		for _, s := range surfaces {
			if s.RoomID != room.ID || len(s.Adjacent) == 0 {
				continue
			}
			sites, err := roomStorageSites(room.Cells, centroid(s.Adjacent), bounds, cells, append(append([]domain.Cell(nil), protected...), s.Adjacent...))
			return room, sites, err
		}
	}
	return policy.Room{}, nil, nil
}

// rawFoodStockSites finds the first planned freezer standing in the census
// and lists the 2x2 patches inside it nearest its door into the kitchen (the
// Link; the outer Door on plans saved before #819). A zero room means no
// planned freezer stands yet.
func rawFoodStockSites(layout policy.LayoutPlan, rooms policy.RoomObservation, bounds policy.Bounds, cells []policy.SiteCell, protected []domain.Cell) (policy.Room, [][]domain.Cell, error) {
	for _, planned := range layout.Rooms {
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
