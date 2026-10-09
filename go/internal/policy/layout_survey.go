package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The map survey is the whole-map read the layout plan is
// derived from, and PlannedRole names a planned room's role.

// SurveyCell is one map cell of the settle-time survey. Absent cells are
// unknown and score as unbuildable.
type SurveyCell struct {
	Cell domain.Cell
	// Walkable is ground a pawn crosses (deep water is not); Rock is
	// natural rock a module is mined out of. Built is a player edifice on
	// the cell (a wall, a door): the perimeter plans as though the colony's
	// own buildings were not there, so a standing wall reads as the ground
	// under it.
	Walkable, Rock, Built bool
	// Footing is what the natural ground holds, under any floor or bridge;
	// walkable ground short of firm is soft and carries no module.
	// Bridgeable takes a bridge; Dries turns firm under a moisture pump (not
	// moving or deep water).
	Footing           Footing
	Bridgeable, Dries bool
	// Hazard is terrain that hurts or contaminates by its own def flags (lava):
	// nothing is built, farmed or walled on it.
	Hazard bool
	// ThickRoof is overhead mountain: no drop pods, no roof collapse from
	// mining, cold storage.
	ThickRoof bool
	// Fertility is the soil's growing multiplier (0 for rock and floors).
	Fertility float64
	// Ore is rock holding a mineable resource; Tree is a cell under a
	// tree.
	Ore, Tree bool
	// Prop is a structure the colony neither owns nor clears as a ruin (an
	// ancient exostrider's remains, a blueprint): no core room or hallway is
	// sited over it.
	Prop bool
	// Ruin is a clearable ruin: not walkable yet, but home clearance
	// deconstructs it, so core rooms may be sited over it.
	Ruin bool
}

// Footing is the heaviest structure a cell's terrain holds.
type Footing uint8

const (
	// FootingFirm takes any building, a stone wall included.
	FootingFirm Footing = iota
	// FootingLight takes light structures only, a wooden wall among them:
	// marshy soil, soft sand, a plain bridge.
	FootingLight
	// FootingNone takes nothing without a bridge: marsh, mud, water.
	FootingNone
)

// Soft is walkable ground no module or stone wall stands on.
func (c SurveyCell) Soft() bool {
	return (c.Walkable || c.Built) && !c.Rock && c.Footing != FootingFirm
}

// MapSurvey is the whole map, scored once at settle time.
type MapSurvey struct {
	Bounds Bounds
	Cells  []SurveyCell
	// Cold is the map's climate (ColdMapCurve), set by the caller; a fresh
	// plan latches it (LayoutPlan.Cold).
	Cold bool
	// Hot is the map's other climate end (HotMapCurve), latched the same way.
	Hot bool
}

// PlannedRole is a planned room's role.
type PlannedRole string

const (
	PlannedHospital PlannedRole = "hospital"
	PlannedPrison   PlannedRole = "prison"
	PlannedKitchen  PlannedRole = "kitchen"
	PlannedFreezer  PlannedRole = "freezer"
	// PlannedButchery is the butcher room (butchering is filthy, so it is
	// kept out of the kitchen): beside the freezer behind a Link door when
	// a side is free, else on the hallway.
	PlannedButchery PlannedRole = "butchery"
	PlannedStorage  PlannedRole = "storage"
	PlannedWorkshop PlannedRole = "workshop"
	// PlannedReserve is sound ground held for growth.
	PlannedReserve PlannedRole = "reserve"
)

// LayoutEdgeMargin is how far from the map edge nothing is built: raiders
// and the map edge's fog make the band useless.
const LayoutEdgeMargin int32 = 10
