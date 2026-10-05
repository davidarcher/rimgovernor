package policy

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The armory and wardrobe stockpiles (#1774, epic #1765): once layout's
// armory (#1773) stands, one zone over its free cells keeps weapons and armor
// there; the same for clothing in the wardrobe. They replace the fixed 2x2
// weapons and apparel zones: until the rooms stand, gear stays in the general
// store. The armor-versus-clothing split is the catalog's (ItemFacts.Armor),
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
// gear floors) and the gear stores' filters. Nil on a StorageRequest while the
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

// demand asks for a gear room once the warehouse can no longer hold what the
// colony has (full, see warehouseReading) and serviceable gear of the room's
// kind is held: weapons and armor ask for the armory, clothing for the
// wardrobe (#1803).
func (g *GearStore) demand(full bool) RoomDemand {
	if g == nil || !full {
		return RoomDemand{}
	}
	return RoomDemand{Armory: g.Weapons+g.ArmorHeld > 0, Wardrobe: g.Clothing > 0}
}

// gearRoomPending reports a gear room demand asks for that the plan holds but
// does not yet stand: it will take gear out of the warehouse, so the
// warehouse waits before asking for another storage room.
func (r StorageRequest) gearRoomPending(demand RoomDemand) bool {
	if r.Layout == nil || r.Rooms == nil {
		return false
	}
	for _, planned := range r.Layout.AllRooms() {
		if planned.Role == PlannedArmory && demand.Armory || planned.Role == PlannedWardrobe && demand.Wardrobe {
			if _, ok := PlannedRoomStanding(planned, *r.Rooms); !ok {
				return true
			}
		}
	}
	return false
}

// ErrArmoryNearPrison is a standing armory with every free cell within the
// weapon clearance of a prison: layout keeps the armory clear of prisons, so
// this names a plan that predates the rule or ground it could not clear.
var ErrArmoryNearPrison = errors.New("the armory has no free cell clear of a prison, so no armory stockpile can be sited")

// gearSites are the armory and wardrobe zones: each standing gear room is
// one site over its free cells. The armory never takes a cell within the
// weapon clearance of a prison (nearPrison); a room whose free cells all are
// is reported (ErrArmoryNearPrison), not silently left unzoned.
func (r StorageRequest) gearSites() ([]StockpileSite, error) {
	if r.Layout == nil || r.Rooms == nil || r.Gear == nil {
		return nil, nil
	}
	prisons := PrisonCells(*r.Layout)
	var out []StockpileSite
	var err error
	for _, gear := range []struct {
		module PlannedRole
		prefix string
		filter domain.StockpileFilter
	}{{PlannedArmory, domain.ArmoryRolePrefix, r.Gear.Armory}, {PlannedWardrobe, domain.WardrobeRolePrefix, r.Gear.Wardrobe}} {
		for _, planned := range r.Layout.AllRooms() {
			if planned.Role != gear.module {
				continue
			}
			room, ok := PlannedRoomStanding(planned, *r.Rooms)
			if !ok || len(room.Cells) == 0 {
				continue
			}
			free := roomPool(room.Cells, r.Cells, r.Protected)
			pool := free
			if gear.module == PlannedArmory {
				pool = nil
				for _, c := range free {
					if !nearPrison([]domain.Cell{c}, prisons) {
						pool = append(pool, c)
					}
				}
				if len(free) > 0 && len(pool) == 0 {
					err = fmt.Errorf("%w (room %s)", ErrArmoryNearPrison, room.ID)
				}
			}
			out = append(out, StockpileSite{Role: gear.prefix + room.ID, Room: room.Cells, Filter: gear.filter, Priority: domain.PreferredPriority, Remainder: true,
				Candidates: [][]domain.Cell{pool}})
			break
		}
	}
	return out, err
}
