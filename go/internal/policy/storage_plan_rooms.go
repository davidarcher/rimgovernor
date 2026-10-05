package policy

// A further warehouse (#1772, epic #1765): when every warehouse zone is at
// or over StockpileGrowFill and none can grow inside its room, the planner
// asks layout for one more storage room (RoomDemand.Storage). It never
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

// storageRoomsWanted is how many storage rooms the plan should hold: 0 for
// no demand, else the planned count plus one. Every planned storage room
// must stand with a warehouse zone in it, every such zone must be at or over
// StockpileGrowFill, and none may have a cell in its room left to grow onto.
// idle reports a true no-demand reading (#1825): a standing room's zone is
// under StockpileGrowFill or can still grow. A 0 without idle is only a
// wait on a planned room not yet built or a zone not yet sited.
func (w warehouseReading) storageRoomsWanted() (wanted int, idle bool) {
	if w.rooms == 0 || w.idle || w.pending {
		return 0, w.idle
	}
	return w.rooms + 1, false
}

// warehouseReading is the warehouse zones' state across the planned storage
// rooms: rooms planned; idle when a standing room's zone is under
// StockpileGrowFill or can still grow; pending when a planned room is not
// standing or holds no warehouse zone yet; full when some warehouse zone
// stands and every one is at or over StockpileGrowFill with no cell left to
// grow onto, whatever else is pending. Full is what the warehouse can no
// longer hold, the demand for the gear rooms (GearStore).
type warehouseReading struct {
	rooms               int
	idle, pending, full bool
}

func (r StorageRequest) warehouseReading() warehouseReading {
	rooms := r.plannedStorageRooms()
	w := warehouseReading{rooms: len(rooms)}
	if len(rooms) == 0 {
		return w
	}
	open := newStockpileOpen(StockpileRequest{Cells: r.Cells, Bounds: r.Bounds, Protected: r.Protected})
	zones := false
	for _, planned := range rooms {
		room, ok := PlannedRoomStanding(planned, *r.Rooms)
		if !ok {
			w.pending = true
			continue
		}
		inRoom := cellSet(room.Cells)
		open.only = inRoom
		served := false
		for _, z := range r.Zones {
			if !isWarehouseRole(z.Role) || !stockpileTouches(z.Cells, inRoom) {
				continue
			}
			served, zones = true, true
			if z.Fill() < StockpileGrowFill {
				w.idle = true
				continue
			}
			if _, grows := stockpileGrowEdit(open, z); grows {
				w.idle = true
			}
		}
		if !served {
			w.pending = true
		}
	}
	w.full = zones && !w.idle
	return w
}

// StorageRoomsOwed is how many storage rooms demand asks for that plan lacks.
func StorageRoomsOwed(plan LayoutPlan, demand RoomDemand) int {
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
// planned so the first storage room stays the core. A room is only ever
// retired by the reconcile steps in layout_retire.go; none moves or resizes.
// It reports whether a room was added.
func growStorageRooms(plan LayoutPlan, demand RoomDemand, sc *planScorer) (LayoutPlan, bool, error) {
	if len(plan.Hallways()) == 0 || StorageRoomsOwed(plan, demand) == 0 {
		return plan, false, nil
	}
	return SiteRoom(plan, sc, ModuleStorage, coreRoomSize[ModuleStorage])
}
