package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The food stockpile (#1777): the colony's food storage zone, a 3x3 inside
// the planned kitchen at its door, declared from the layout plan once and sited
// as soon as the kitchen's interior is open (#2219).

const (
	// foodSiteSide is the food zone's side: nine cells, the least that
	// counts as food storage.
	foodSiteSide int32 = 3
)

// FoodStore states the colony's food storage is not met, so the kitchen holds
// a food stockpile.
type FoodStore struct{}

// foodStore is the food stockpile: a 3x3 inside the first planned kitchen,
// nearest its door.
func (r StorageRequest) foodStore() (Store, bool) {
	if r.Food == nil || r.Layout == nil {
		return Store{}, false
	}
	for _, kitchen := range r.Layout.AllRooms() {
		if kitchen.Role == PlannedKitchen {
			return Store{StoreSite: StoreSite{Role: domain.FoodRole, Interior: kitchen.Interior, Width: foodSiteSide, Height: foodSiteSide, Anchor: kitchen.Door,
				Filter: domain.FoodFilter(), Priority: domain.PreferredPriority}}, true
		}
	}
	return Store{}, false
}
