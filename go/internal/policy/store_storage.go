package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The Storage department's stores (#2192, epic #2176): the warehouse over the
// whole interior of each planned storage room, and the materials yard over the
// whole interior of each planned yard. Each is one zone, sized once; a full
// store asks layout for a further room through RoomDemand.

// storageOwner is the Storage department.
type storageOwner struct{}

func (storageOwner) Department() Department { return DepartmentStorage }

// Stores are the warehouses then the yards, in plan order. The warehouse is
// Low priority so the workstation stockpiles draw first; every store of a role shares its key, the zone
// matching its room by position.
func (storageOwner) Stores(v StoreView) []Store {
	if v.Layout == nil {
		return nil
	}
	var out []Store
	for _, room := range v.plannedStorageRooms() {
		out = append(out, Store{
			StoreSite: StoreSite{Role: domain.GeneralRole, Interior: room.Interior, Filter: domain.GeneralFilter(), Priority: domain.LowPriority},
			Further:   PlannedStorage,
		})
	}
	for _, room := range v.Layout.YardRooms() {
		out = append(out, Store{
			StoreSite: StoreSite{Role: domain.YardRole, Interior: room.Interior, Filter: domain.YardFilter(), Priority: domain.LowPriority},
			Further:   PlannedYard,
		})
	}
	return out
}

// RoomDemand is DeclaredDemand over the stores, held while a gear room the
// plan holds is not yet standing: it will take gear out of the warehouse.
func (o storageOwner) RoomDemand(v StoreView) RoomDemand {
	demand := DeclaredDemand(v, o.Stores(v))
	if v.Layout != nil && v.Rooms != nil && v.gearRoomPending(militaryOwner{}.RoomDemand(v)) {
		demand.Storage = 0
	}
	return demand
}
