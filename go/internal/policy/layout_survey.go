package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The map survey (#727) is the whole-map read the layout plan (#771) is
// derived from, and ModuleRole names a planned room's role.

// SurveyCell is one map cell of the settle-time survey. Absent cells are
// unknown and score as unbuildable.
type SurveyCell struct {
	Cell domain.Cell
	// Walkable is ground a pawn crosses (deep water is not); Rock is
	// natural rock a module is mined out of. Built is a player edifice on
	// the cell (a wall, a door): the perimeter plans as though the colony's
	// own buildings were not there, so a standing wall reads as the ground
	// under it (#954).
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
	// tree (#778).
	Ore, Tree bool
	// Prop is a structure the colony neither owns nor clears as a ruin (an
	// ancient exostrider's remains, a blueprint): no core room or hallway is
	// sited over it (#1533).
	Prop bool
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
}

// ModuleRole is a planned room's role.
type ModuleRole string

const (
	ModuleHospital ModuleRole = "hospital"
	ModulePrison   ModuleRole = "prison"
	ModuleKitchen  ModuleRole = "kitchen"
	ModuleFreezer  ModuleRole = "freezer"
	// ModuleButchery is the butcher room (butchering is filthy, so it is
	// kept out of the kitchen): beside the freezer behind a Link door when
	// a side is free, else on the hallway.
	ModuleButchery ModuleRole = "butchery"
	ModuleStorage  ModuleRole = "storage"
	ModuleWorkshop ModuleRole = "workshop"
	// ModuleReserve is sound ground held for growth.
	ModuleReserve ModuleRole = "reserve"
)

// LayoutEdgeMargin is how far from the map edge nothing is built: raiders
// and the map edge's fog make the band useless.
const LayoutEdgeMargin int32 = 10
