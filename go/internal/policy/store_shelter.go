package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The Shelter department's one store: the starter shelter's floor as a general
// store at the lowest priority, so the colony has somewhere to put its things
// before it has a storage room. The zone covers the whole interior; furniture
// placed on top of it (RimWorld allows it) shrinks the zone by itself. Any real
// store outranks it, and the zone is deleted (its contents haul to the better
// stores) once a storage room or freezer stands, or the shelter leaves the plan.

// shelterOwner is the Shelter department.
type shelterOwner struct{}

func (shelterOwner) Department() Department { return DepartmentShelter }

// Stores are the shelter rooms' general stores while no storage room stands.
// Nothing is declared while the layout plan is unread; a plan with no shelter
// or a standing storage room declares the role retired, which deletes the
// zone. While the room census is unread the stores stay: absence of a reading
// is not a standing room.
func (shelterOwner) Stores(v StoreView) []Store {
	if v.Layout == nil {
		return nil
	}
	retired := Store{Retired: true, StoreSite: StoreSite{Role: domain.ShelterRole}}
	if v.storageRoomStands() {
		return []Store{retired}
	}
	var out []Store
	for _, room := range v.Layout.AllRooms() {
		if room.Role == PlannedShelter {
			out = append(out, Store{StoreSite: StoreSite{Role: domain.ShelterRole, Interior: room.Interior, Filter: domain.GeneralFilter(), Priority: domain.LowPriority}})
		}
	}
	if len(out) == 0 {
		return []Store{retired}
	}
	return out
}

func (shelterOwner) RoomDemand(StoreView) RoomDemand { return RoomDemand{} }

// storageRoomStands is whether a planned storage room or freezer is a roofed
// room the census sees: the colony has a real store the shelter's floor yields to.
func (r StoreView) storageRoomStands() bool {
	if r.Layout == nil || r.Rooms == nil {
		return false
	}
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role != PlannedStorage && planned.Role != PlannedFreezer {
			continue
		}
		if _, ok := CensusRoomIn(planned, *r.Rooms); ok {
			return true
		}
	}
	return false
}
