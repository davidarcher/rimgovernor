package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The storage planner (#1765, #1769): one deterministic function from the
// colony view to the full desired set of room-bound storage sites. Planner
// output is derived state, recomputed every pass in Go memory; the standing
// zones are the only record, and MaintainStockpiles applies the diff
// (StockpileRequest.Sited). The role registry supplies each role's filter
// and priority; a site names them for the zones it creates.

// MealStore is where the meal stockpile belongs, resolved by the caller from
// the dining facts (a standing meal closet, the freezer's door into the
// dining room, or the census dining table).
type MealStore struct {
	Room   Room
	Filter domain.StockpileFilter
	// Anchor and Avoid place a one-cell store: the cell nearest Anchor off
	// Avoid.
	Anchor domain.Cell
	Avoid  []domain.Cell
	// Whole makes the zone the whole room.
	Whole bool
}

// StorageRequest is the colony view the planner reads. Layout and Rooms are
// nil while the layout plan or the room census is unknown, and no site that
// needs them is planned.
type StorageRequest struct {
	Bounds    Bounds
	Cells     []SiteCell
	Protected []domain.Cell
	Layout    *LayoutPlan
	Rooms     *RoomObservation
	// Meals is nil when no meal store is wanted or its facts are unknown.
	Meals *MealStore
	// BenchInputs are the benches consuming stored inputs (#1775), and
	// Benches the benches standing (unknown holds every bench store); both
	// are read by the Industry department's stores.
	BenchInputs []BenchInput
	Benches     domain.Fact[map[string]bool]
	// Sleeping is nil while the bed census is unknown; the medicine store
	// needs it with Rooms.
	Sleeping *SleepingObservation
	// Food is nil when the colony's food storage stands or its fact is
	// known to be met; see FoodStore.
	Food *FoodStore
	// Gear is the serviceable gear held and the gear stores' filters; nil
	// while the gear census is unread (see GearStore).
	Gear *GearStore
	// Zones are the standing stockpile zones, read for the warehouse siting.
	Zones []StockpileZone
	// Burial is the burial census; nil while the waste or construction
	// census is unread (see BurialCensus).
	Burial *BurialCensus
	// Incinerator is the planned incinerator room once its walls and door
	// stand (#1814); nil before. The Sanitation store declares its zone.
	Incinerator *PlannedRoom
}

// RoomDemand is the planner's signal to layout that stored goods outgrew
// the warehouse (#1773, #1774): the armory for weapons and armor, the
// wardrobe for clothing. The planner never plans the rooms; layout adds them
// (GearRoomsOwed).
type RoomDemand struct {
	Armory, Wardrobe bool
	// Storage is the storage rooms the plan should hold, 0 for no demand
	// (a further warehouse, #1772).
	Storage int
	// Graveyards is the graveyards the plan should hold, 0 for no demand (a
	// further graveyard, #2196; see GraveyardsWanted).
	Graveyards int
	// Yard is the materials yards the plan should hold, 0 for no demand
	// (a further yard, #2192; see YardRoomsWanted).
	Yard int
	// Known is set when the gear census was read, so a false Armory or
	// Wardrobe is a reading and not a gap (#1825). StorageIdle is set when a
	// standing storage room has warehouse space to spare: a true no-demand
	// reading, unlike a Storage of 0 that waits on a planned room not yet
	// built.
	Known, StorageIdle bool
}

// StoragePlan is the desired storage, most important site first.
type StoragePlan struct {
	Sites      []StockpileSite
	RoomDemand RoomDemand
	// Err joins the sites the planner could not make usable (a room that
	// stands but cannot host its store, ErrArmoryNearPrison); the rest of the
	// plan stands.
	Err error
}

// PlanStorage returns the desired storage sites. Every store is declared by its
// department (DeclareStores), so no site is left for it to plan; it goes with
// the role registry (#2206).
func PlanStorage(r StorageRequest) StoragePlan {
	return StoragePlan{}
}
