package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The armory and wardrobe stockpiles (#1774, epic #1765): once layout's
// armory (#1773) stands, one zone over its free cells keeps weapons and armor
// there; the same for clothing in the wardrobe. They replace the fixed 2x2
// weapons and apparel zones: until the rooms stand, gear stays in the general
// store (the stores are the Military department's, store_military.go). The armor-versus-clothing split is the catalog's (ItemFacts.Armor),
// and the gear quality and hit-point floors ride the filters
// (domain.ArmoryFilter, domain.WardrobeFilter).

// ErrNoArmorDefs is the catalog naming no armor def: without the armor split
// the armory and wardrobe have no filters, so gear storage cannot be planned.
var ErrNoArmorDefs = errors.New("the catalog lists no armor defs (ApparelProperties.defaultOutfitTags Soldier without Worker), so the armory and wardrobe cannot be planned")

// GearFilters are the armory's and the wardrobe's filters for the catalog's
// armor defs.
func GearFilters(armor []Resource) (armory, wardrobe domain.StockpileFilter, err error) {
	defs := make([]string, len(armor))
	for i, def := range armor {
		defs[i] = string(def)
	}
	if armory, err = domain.ArmoryFilter(defs); err != nil {
		return armory, wardrobe, err
	}
	wardrobe, err = domain.WardrobeFilter(defs)
	return armory, wardrobe, err
}

// GearStore is the colony's serviceable gear (hit points and quality over the
// gear floors) and the gear stores' filters. Nil on a StoreView while the
// gear census is unread: neither gear room is then asked for or stocked.
type GearStore struct {
	Armory, Wardrobe domain.StockpileFilter
	// Weapons, ArmorHeld and Clothing count the serviceable weapons, armor
	// and clothing the colony holds, loose or stored.
	Weapons, ArmorHeld, Clothing int
}

// NewGearStore counts the serviceable stored apparel of stored by the
// catalog's armor split, beside the weapons lying on the map. It fails with
// ErrNoArmorDefs while the catalog names no armor, and when the armor defs do
// not make valid filters.
func NewGearStore(items ItemFacts, stored []GearStock, weapons int) (GearStore, error) {
	if len(items.Armor) == 0 {
		return GearStore{}, ErrNoArmorDefs
	}
	g := GearStore{Weapons: weapons}
	var err error
	if g.Armory, g.Wardrobe, err = GearFilters(items.Armor); err != nil {
		return GearStore{}, err
	}
	armor := map[Resource]bool{}
	for _, def := range items.Armor {
		armor[def] = true
	}
	for _, row := range stored {
		switch {
		case !row.Serviceable():
		case armor[row.Definition]:
			g.ArmorHeld += row.Count
		default:
			g.Clothing += row.Count
		}
	}
	return g, nil
}

// gearRoomPending reports a gear room demand asks for that the plan holds but
// does not yet stand: it will take gear out of the warehouse, so the
// warehouse waits before asking for another storage room.
func (r StoreView) gearRoomPending(demand RoomDemand) bool {
	if r.Layout == nil || r.Rooms == nil {
		return false
	}
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == PlannedArmory && demand.Armory || planned.Role == PlannedWardrobe && demand.Wardrobe {
			// Census: pending until the room is a roofed room a zone can bind to.
			if _, ok := CensusRoomIn(planned, *r.Rooms); !ok {
				return true
			}
		}
	}
	return false
}
