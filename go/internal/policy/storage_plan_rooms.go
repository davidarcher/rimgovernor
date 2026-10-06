package policy

// A further warehouse (#1772, epic #1765): when every warehouse zone is at
// or over StockpileFurtherRoomFill, the Storage department asks layout for one more
// storage room (RoomDemand.Storage). It never plans the room; once the room
// stands its warehouse zone is created and the demand clears until that zone
// fills too.

// plannedStorageRooms are the plan's storage rooms in plan order, the first
// being the core room.
func (r StorageRequest) plannedStorageRooms() []PlannedRoom {
	if r.Layout == nil {
		return nil
	}
	var out []PlannedRoom
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == PlannedStorage {
			out = append(out, planned)
		}
	}
	return out
}

// StorageRoomsOwed is how many storage rooms demand asks for that plan lacks.
func StorageRoomsOwed(plan LayoutPlan, demand RoomDemand) int {
	have := 0
	for _, r := range plan.AllRooms() {
		if r.Role == PlannedStorage {
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
	return SiteRoom(plan, sc, PlannedStorage, coreRoomSize[PlannedStorage])
}
