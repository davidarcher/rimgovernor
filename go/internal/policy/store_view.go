package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The colony view the departments declare their stores from (DeclareStores).
// Declarations are derived state, recomputed every pass in Go memory; the
// standing zones are the only record, and MaintainStockpiles applies the diff.

// MealStore is the one-cell meal store by the dining table, resolved by the
// caller from the dining facts: the table is a built-furniture fact, so this
// is the one store sited from more than the layout plan.
type MealStore struct {
	// Dining is the planned dining room's interior.
	Dining Rectangle
	Filter domain.StockpileFilter
	// Anchor and Avoid place the cell: nearest Anchor, off Avoid (the chairs).
	Anchor domain.Cell
	Avoid  []domain.Cell
	// Retired states the colony no longer wants a warm spot by the table.
	Retired bool
}

// StoreView is the colony view the departments read. Layout and Rooms are
// nil while the layout plan or the room census is unknown, and no store that
// needs them is declared.
type StoreView struct {
	Bounds    Bounds
	Cells     []SiteCell
	Protected []domain.Cell
	Layout    *LayoutPlan
	Rooms     *RoomObservation
	// Meals is nil when the table meal store's facts are unknown.
	Meals *MealStore
	// BenchInputs are the benches consuming stored inputs, and
	// Benches the benches standing (unknown holds every bench store); both
	// are read by the Industry department's stores.
	BenchInputs []BenchInput
	Benches     domain.Fact[map[string]bool]
	// Shapes are the piece shapes the hospital template plans its beds with
	// (the medicine store sits nearest them).
	Shapes PieceShapes
	// Gear is the serviceable gear held and the gear stores' filters; nil
	// while the gear census is unread (see GearStore).
	Gear *GearStore
	// Zones are the standing stockpile zones, read for the warehouse siting.
	Zones []StockpileZone
	// Burial is the burial census; nil while the waste or construction
	// census is unread (see BurialCensus).
	Burial *BurialCensus
	// TrainingGap is the standing skill gap (see TrainingGap); the Military
	// store asks for a training range while it is open.
	TrainingGap domain.Fact[bool]
	// Incinerator is the planned incinerator room once its walls and door
	// stand; nil before. The Sanitation store declares its zone.
	Incinerator *PlannedRoom
	// AnimalFeed is the herds' feed and the barn's sleeping spot shape; nil
	// while either is unread. The People animal store declares the barn feed
	// stockpiles from it.
	AnimalFeed *AnimalFeedStore
}

// RoomDemand is the departments' signal to layout that stored goods outgrew
// the warehouse: the armory for weapons and armor, the
// wardrobe for clothing. The departments never plan the rooms; layout adds them
// (GearRoomsOwed).
type RoomDemand struct {
	Armory, Wardrobe bool
	// Storage is the storage rooms the plan should hold, 0 for no demand
	// (a further warehouse).
	Storage int
	// Graveyards is the graveyards the plan should hold, 0 for no demand (a
	// further graveyard; see GraveyardsWanted).
	Graveyards int
	// Ranges is the training ranges the plan should hold, 0 for no demand (the
	// training concern, #2619).
	Ranges int
	// Yard is the materials yards the plan should hold, 0 for no demand
	// (a further yard; see YardRoomsWanted).
	Yard int
	// Known is set when the gear census was read, so a false Armory or
	// Wardrobe is a reading and not a gap. StorageIdle is set when a
	// standing storage room has warehouse space to spare: a true no-demand
	// reading, unlike a Storage of 0 that waits on a planned room not yet
	// built.
	Known, StorageIdle bool
}
