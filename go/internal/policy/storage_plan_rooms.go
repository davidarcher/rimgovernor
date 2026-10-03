package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// A further warehouse (#1772, epic #1765): when every warehouse zone is at
// or over StockpileGrowFill and none can grow inside its room, the planner
// asks layout for one more storage room (GearRoomDemand.Storage). It never
// plans the room; once the room stands the planner sites its warehouse zone
// (warehouseSites) and the demand clears until that zone fills too.

// plannedStorageRooms are the plan's storage rooms in plan order, the first
// being the core room.
func (r StorageRequest) plannedStorageRooms() []LayoutRoom {
	if r.Layout == nil || r.Rooms == nil {
		return nil
	}
	var out []LayoutRoom
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == ModuleStorage {
			out = append(out, planned)
		}
	}
	return out
}

// isWarehouseRole reports the first warehouse's role or a further one's.
func isWarehouseRole(role string) bool { return stockpileRolePrefix(role) == domain.GeneralRole }

// storageRoomsWanted is how many storage rooms the plan should hold: 0 for
// no demand, else the planned count plus one. Every planned storage room
// must stand with a warehouse zone in it, every such zone must be at or over
// StockpileGrowFill, and none may have a cell in its room left to grow onto.
// idle reports a true no-demand reading (#1825): a standing room's zone is
// under StockpileGrowFill or can still grow. A 0 without idle is only a
// wait on a planned room not yet built or a zone not yet sited.
func (r StorageRequest) storageRoomsWanted() (wanted int, idle bool) {
	rooms := r.plannedStorageRooms()
	if len(rooms) == 0 {
		return 0, false
	}
	open := newStockpileOpen(StockpileRequest{Cells: r.Cells, Bounds: r.Bounds, Protected: r.Protected})
	pending := false
	for _, planned := range rooms {
		room, ok := PlannedRoomStanding(planned, *r.Rooms)
		if !ok {
			pending = true
			continue
		}
		inRoom := cellSet(room.Cells)
		open.only = inRoom
		served := false
		for _, z := range r.Zones {
			if !isWarehouseRole(z.Role) || !stockpileTouches(z.Cells, inRoom) {
				continue
			}
			served = true
			if z.Fill() < StockpileGrowFill {
				idle = true
				continue
			}
			if _, grows := stockpileGrowEdit(open, z); grows {
				idle = true
			}
		}
		if !served {
			pending = true
		}
	}
	if idle || pending {
		return 0, idle
	}
	return len(rooms) + 1, false
}

// StorageRoomsOwed is how many storage rooms demand asks for that plan lacks.
func StorageRoomsOwed(plan LayoutPlan, demand GearRoomDemand) int {
	have := 0
	for _, r := range plan.AllRooms() {
		if r.Role == ModuleStorage {
			have++
		}
	}
	return max(demand.Storage-have, 0)
}

// growStorageRooms adds the storage room demand asks for and plan lacks, on
// the nearest core slot like any other core room, after the rooms already
// planned so the first storage room stays the core. It reports whether a
// room was added.
func growStorageRooms(plan LayoutPlan, demand GearRoomDemand) (LayoutPlan, bool) {
	if len(plan.Spine) == 0 || StorageRoomsOwed(plan, demand) == 0 {
		return plan, false
	}
	return growModuleRoom(plan, ModuleStorage, [][2]int32{coreRoomSize[ModuleStorage]})
}
