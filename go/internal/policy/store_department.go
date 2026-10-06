package policy

import "errors"

// A department that owns stockpiles is an entity (epic #2176, #2191): it
// declares its Stores and publishes its RoomDemand, and MaintainStockpiles
// applies every declaration in one pass. A department that owns no store stays
// a grouping tag. A store migrating out of PlanStorage is declared here once,
// and the one definition serves create, retarget and retire.

// Store is one declared stockpile: its site (role, planned room or clean
// rectangle inside it, filter, priority) and what the department does when it
// is full or gone. It is sized once; the pass never grows, shrinks or merges
// its zone.
type Store struct {
	StoreSite
	// Further is the planned room the department asks layout for when the
	// store's standing zones are at StockpileGrowFill (PlannedStorage,
	// PlannedArmory or PlannedWardrobe); empty asks for none.
	Further PlannedRole
	// Retired states the store's purpose is gone (a bench demolished): the
	// zones of its role are deleted. Absence is never retirement, so a
	// department that cannot read its room yet simply declares nothing.
	Retired bool
}

// StoreOwner is a department that declares stockpiles. The view is the storage
// planner's request: layout plan, room census, open ground and standing zones.
type StoreOwner interface {
	Department() Department
	// Stores are the department's declared stores, most important first.
	Stores(view StorageRequest) []Store
	// RoomDemand is the department's ask of layout, from capacity: usually
	// DeclaredDemand over its own stores.
	RoomDemand(view StorageRequest) RoomDemand
}

// storeOwners is the one registry of departments that declare stores.
var storeOwners = []StoreOwner{storageOwner{}, militaryOwner{}}

// storageOwner is the Storage department: the warehouse and materials yard
// migrate onto it (#2192); it declares none yet.
type storageOwner struct{}

func (storageOwner) Department() Department        { return DepartmentStorage }
func (storageOwner) Stores(StorageRequest) []Store { return nil }
func (o storageOwner) RoomDemand(v StorageRequest) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}

// StoreDeclaration is every owner's stores and room demand, merged.
type StoreDeclaration struct {
	Stores []Store
	Demand RoomDemand
	// Err joins what an owner could not site (ErrArmoryNearPrison).
	Err error
	// covers are the room roles whose demand a declared store now owns; the
	// fill-based reading of PlanStorage yields for exactly these.
	covers map[PlannedRole]bool
}

// DeclareStores collects the registered departments' declarations.
func DeclareStores(view StorageRequest) StoreDeclaration {
	return declareStores(storeOwners, view)
}

func declareStores(owners []StoreOwner, view StorageRequest) StoreDeclaration {
	d := StoreDeclaration{covers: map[PlannedRole]bool{}}
	for _, o := range owners {
		stores := o.Stores(view)
		for _, s := range stores {
			if s.Further != "" && !s.Retired {
				d.covers[s.Further] = true
			}
		}
		d.Stores = append(d.Stores, stores...)
		if e, ok := o.(interface{ RoomsAsked() []PlannedRole }); ok {
			for _, role := range e.RoomsAsked() {
				d.covers[role] = true
			}
		}
		if e, ok := o.(interface{ StoreErr(StorageRequest) error }); ok {
			d.Err = errors.Join(d.Err, e.StoreErr(view))
		}
		got := o.RoomDemand(view)
		d.Demand.Armory = d.Demand.Armory || got.Armory
		d.Demand.Wardrobe = d.Demand.Wardrobe || got.Wardrobe
		d.Demand.Storage = max(d.Demand.Storage, got.Storage)
		d.Demand.StorageIdle = d.Demand.StorageIdle || got.StorageIdle
		d.Demand.Known = d.Demand.Known || got.Known
	}
	return d
}

// Apply is the layout demand once the declared stores answer for their rooms:
// a covered room takes the declared reading, the rest keeps old.
func (d StoreDeclaration) Apply(old RoomDemand) RoomDemand {
	if d.covers[PlannedStorage] {
		old.Storage, old.StorageIdle = d.Demand.Storage, d.Demand.StorageIdle
	}
	if d.covers[PlannedArmory] {
		old.Armory = d.Demand.Armory
	}
	if d.covers[PlannedWardrobe] {
		old.Wardrobe = d.Demand.Wardrobe
	}
	old.Known = old.Known || d.Demand.Known
	return old
}

// StoreReading is a store's capacity now: Standing when a zone serves it, Full
// when every serving zone is at StockpileGrowFill. A sized-once store has no
// room to grow onto, so full is the whole reading.
type StoreReading struct{ Standing, Full bool }

// Reading reads the store against the standing zones.
func (s Store) Reading(zones []StockpileZone) StoreReading {
	r := StoreReading{Full: true}
	for _, z := range zones {
		if s.serves(z) {
			r.Standing = true
			r.Full = r.Full && z.Fill() >= StockpileGrowFill
		}
	}
	r.Full = r.Full && r.Standing
	return r
}

// DeclaredDemand is the capacity-based room demand of stores: an armory or
// wardrobe store at capacity asks for its room; storage stores ask for one
// more room than the plan holds once every one stands and is full. A store
// whose room does not stand yet is a wait, never a demand or an idle reading.
func DeclaredDemand(view StorageRequest, stores []Store) RoomDemand {
	var demand RoomDemand
	storage, full, idle, pending := 0, 0, false, false
	for _, s := range stores {
		if s.Retired || s.Further == "" {
			continue
		}
		demand.Known = true
		reading := s.Reading(view.Zones)
		switch s.Further {
		case PlannedStorage:
			storage++
			switch {
			case !reading.Standing:
				pending = true
			case reading.Full:
				full++
			default:
				idle = true
			}
		case PlannedArmory:
			demand.Armory = demand.Armory || reading.Full
		case PlannedWardrobe:
			demand.Wardrobe = demand.Wardrobe || reading.Full
		}
	}
	demand.StorageIdle = idle
	if storage > 0 && !pending && !idle && full == storage {
		demand.Storage = len(view.plannedStorageRooms()) + 1
	}
	return demand
}

// state is a declared store's desired state for a zone it serves.
func (s Store) state() StockpileRoleState {
	return StockpileRoleState{Filter: s.Filter, Priority: s.Priority, Retired: s.Retired, Fixed: true}
}

// declaredStoreEdits are the edits of the declared stores, one set of rules:
// a zone outside every live site of its role is deleted once the replacement
// stands (create before delete), a zone of a retired role is deleted, one
// whose filter or priority differs is retargeted, and a store no zone serves
// gets its zone created. Zones of declared stores are returned in touched so
// the pass never grows, shrinks or merges them.
func declaredStoreEdits(zones []StockpileZone, stores []Store, open stockpileOpen) (edits []StockpileEdit, touched map[string]bool) {
	touched = map[string]bool{}
	var live []StoreSite
	for _, s := range stores {
		if !s.Retired {
			live = append(live, s.StoreSite)
		}
	}
	for _, e := range storeSiteMoves(zones, live) {
		touched[e.Zone] = true
		edits = append(edits, e)
	}
	for _, z := range zones {
		for _, s := range stores {
			if !s.roleOf(z) || touched[z.ID] {
				continue
			}
			if s.Retired || s.serves(z) {
				touched[z.ID] = true
				if e, ok := stockpileSettingsEdit(func(string) (StockpileRoleState, bool) { return s.state(), true }, z); ok {
					edits = append(edits, e)
				}
				break
			}
		}
	}
	edits = append(edits, storeSiteEdits(zones, live, open)...)
	return edits, touched
}

// roleOf reports a zone carrying the store's role (the prefix, or the whole
// key for an exact site), wherever it stands.
func (s Store) roleOf(z StockpileZone) bool {
	if z.Role == "" {
		return false
	}
	if s.exact {
		return z.Role == s.Role
	}
	return stockpileRolePrefix(z.Role) == stockpileRolePrefix(s.Role)
}
