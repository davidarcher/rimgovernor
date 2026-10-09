package policy

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Military department owns the armory and wardrobe stores: each standing gear room is one store over its free cells, and the
// gear RoomDemand is its capacity reading: a gear room is asked for while none
// stands and serviceable gear of its kind is held, and again when every store
// of its kind is full. Until the rooms stand, gear stays in the general store.

// ErrArmoryNearPrison is a standing armory with every free cell within the
// weapon clearance of a prison: layout keeps the armory clear of prisons, so
// this names a plan that predates the rule or ground it could not clear.
var ErrArmoryNearPrison = errors.New("the armory has no free cell clear of a prison, so no armory stockpile can be sited")

type militaryOwner struct{}

func (militaryOwner) Department() Department { return DepartmentMilitary }

func (o militaryOwner) Stores(view StoreView) []Store {
	stores, _ := o.gearStores(view)
	return stores
}

// RoomsAsked are the rooms the department asks layout for even before any
// store stands: its reading owns the gear RoomDemand.
func (militaryOwner) RoomsAsked() []PlannedRole { return gearRooms }

// StoreErr names a standing armory no store can be sited in (ErrArmoryNearPrison).
func (o militaryOwner) StoreErr(view StoreView) error {
	_, err := o.gearStores(view)
	return err
}

// RoomDemand asks for a gear room while none stands and serviceable gear of
// its kind is held (weapons and armor for the armory, clothing for the
// wardrobe), and for a further one when every store of the kind is full.
func (o militaryOwner) RoomDemand(view StoreView) RoomDemand {
	g := view.Gear
	if g == nil || view.Layout == nil || view.Rooms == nil {
		return RoomDemand{Known: g != nil}
	}
	demand := DeclaredDemand(view, o.Stores(view))
	demand.Known = true
	stands := map[PlannedRole]bool{}
	for _, planned := range view.Layout.AllRooms() {
		if _, ok := CensusRoomIn(planned, *view.Rooms); ok {
			stands[planned.Role] = true
		}
	}
	demand.Armory = demand.Armory || !stands[PlannedArmory] && g.Weapons+g.ArmorHeld > 0
	demand.Wardrobe = demand.Wardrobe || !stands[PlannedWardrobe] && g.Clothing > 0
	return demand
}

// gearStores are the armory and wardrobe stores: the first standing room of
// each kind, over its free cells. The armory never takes a cell within the
// weapon clearance of a prison (nearPrison); a room whose free cells all are is
// reported (ErrArmoryNearPrison), not silently left without a store.
func (militaryOwner) gearStores(r StoreView) ([]Store, error) {
	if r.Layout == nil || r.Rooms == nil || r.Gear == nil {
		return nil, nil
	}
	prisons := PrisonCells(*r.Layout)
	var out []Store
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
			// Census: the stockpile zone is the room's own cells.
			room, ok := CensusRoomIn(planned, *r.Rooms)
			if !ok || len(room.Cells) == 0 {
				continue
			}
			site := StoreSite{Role: gear.prefix + room.ID, Interior: planned.Interior, Filter: gear.filter, Priority: domain.PreferredPriority}
			if gear.module == PlannedArmory {
				free := roomPool(room.Cells, r.Cells, r.Protected)
				var pool []domain.Cell
				for _, c := range free {
					if !nearPrison([]domain.Cell{c}, prisons) {
						pool = append(pool, c)
					}
				}
				if len(free) > 0 && len(pool) == 0 {
					err = fmt.Errorf("%w (room %s)", ErrArmoryNearPrison, room.ID)
					break
				}
				if len(pool) < len(free) {
					site.regions = cellRects(cellSet(pool))
				}
			}
			out = append(out, Store{StoreSite: site, Further: gear.module})
			break
		}
	}
	return out, err
}
