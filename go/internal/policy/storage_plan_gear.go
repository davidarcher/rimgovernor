package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The armory and wardrobe stockpiles (#1774, epic #1765): once layout's
// armory (#1773) stands, one zone over its free cells keeps weapons and armor
// there; the same for clothing in the wardrobe. They replace the fixed 2x2
// weapons and apparel zones: until the rooms stand, gear stays in the general
// store. The armor-versus-clothing split is the catalog's (ItemFacts.Armor),
// and the gear quality and hit-point floors ride the filters
// (domain.ArmoryFilter, domain.WardrobeFilter).

// gearRoomMinItems is the serviceable gear of a kind that asks layout for its
// room: the cells of the 2x2 zone it replaces (StockpileMinCells).
const gearRoomMinItems = StockpileMinCells

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
// gear floors) and the gear stores' filters. Nil on a StorageRequest while
// the catalog names no armor: neither gear room is then asked for or stocked.
type GearStore struct {
	Armory, Wardrobe domain.StockpileFilter
	// Weapons, ArmorHeld and Clothing count the serviceable weapons, armor
	// and clothing the colony holds, loose or stored.
	Weapons, ArmorHeld, Clothing int
}

// NewGearStore counts the serviceable stored apparel of stored by the
// catalog's armor split, beside the weapons lying on the map. False while
// the catalog names no armor (a frame without its stat table); an error when
// the armor defs do not make valid filters.
func NewGearStore(items ItemFacts, stored []GearStock, weapons int) (GearStore, bool, error) {
	if len(items.Armor) == 0 {
		return GearStore{}, false, nil
	}
	g := GearStore{Weapons: weapons}
	var err error
	if g.Armory, g.Wardrobe, err = GearFilters(items.Armor); err != nil {
		return GearStore{}, false, err
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
	return g, true, nil
}

// demand reads enough serviceable gear of a kind as stored gear outgrowing
// the warehouse's share: weapons and armor ask for the armory, clothing for
// the wardrobe.
func (g *GearStore) demand() RoomDemand {
	if g == nil {
		return RoomDemand{}
	}
	return RoomDemand{Armory: g.Weapons+g.ArmorHeld >= gearRoomMinItems, Wardrobe: g.Clothing >= gearRoomMinItems}
}

// gearSites are the armory and wardrobe zones: each standing gear room is
// one site over its free cells. The armory never takes a cell within the
// weapon clearance of a prison (nearPrison).
func (r StorageRequest) gearSites() []StockpileSite {
	if r.Layout == nil || r.Rooms == nil || r.Gear == nil {
		return nil
	}
	prisons := PrisonCells(*r.Layout)
	var out []StockpileSite
	for _, gear := range []struct {
		module ModuleRole
		prefix string
		filter domain.StockpileFilter
	}{{ModuleArmory, domain.ArmoryRolePrefix, r.Gear.Armory}, {ModuleWardrobe, domain.WardrobeRolePrefix, r.Gear.Wardrobe}} {
		for _, planned := range r.Layout.AllRooms() {
			if planned.Role != gear.module {
				continue
			}
			room, ok := PlannedRoomStanding(planned, *r.Rooms)
			if !ok || len(room.Cells) == 0 {
				continue
			}
			var pool []domain.Cell
			for _, c := range roomPool(room.Cells, r.Cells, r.Protected) {
				if gear.module != ModuleArmory || !nearPrison([]domain.Cell{c}, prisons) {
					pool = append(pool, c)
				}
			}
			out = append(out, StockpileSite{Role: gear.prefix + room.ID, Room: room.Cells, Filter: gear.filter, Priority: domain.PreferredPriority, Remainder: true,
				Candidates: [][]domain.Cell{pool}})
			break
		}
	}
	return out
}
