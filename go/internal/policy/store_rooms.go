package policy

// A further warehouse: when every warehouse zone is at
// or over StockpileFurtherRoomFill, the Storage department asks layout for one more
// storage room (RoomDemand.Storage). It never plans the room; once the room
// stands its warehouse zone is created and the demand clears until that zone
// fills too.

// plannedStorageRooms are the plan's storage rooms in plan order, the first
// being the core room.
func (r StoreView) plannedStorageRooms() []PlannedRoom {
	if r.Layout == nil {
		return nil
	}
	var out []PlannedRoom
	for _, planned := range r.Layout.Rooms {
		if planned.Role == PlannedStorage {
			out = append(out, planned)
		}
	}
	return out
}

// storageRoomChains is how many storage rooms the plan holds, a room and its
// successors counting as one.
func (r StoreView) storageRoomChains() int {
	if r.Layout == nil {
		return 0
	}
	return r.Layout.RoomChains(PlannedStorage)
}
