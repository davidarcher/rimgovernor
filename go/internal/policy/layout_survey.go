package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The map survey (#727) is the whole-map read the layout plan (#771) is
// derived from, and ModuleRole names a planned room's role.

// SurveyCell is one map cell of the settle-time survey. Absent cells are
// unknown and score as unbuildable.
type SurveyCell struct {
	Cell domain.Cell
	// Walkable is open ground a wall can stand on; Rock is natural rock a
	// module is mined out of; Marsh is marsh, mud or shallow water no
	// module is built on.
	Walkable, Rock, Marsh bool
	// ThickRoof is overhead mountain: no drop pods, no roof collapse from
	// mining, cold storage.
	ThickRoof bool
	// Fertility is the soil's growing multiplier (0 for rock and floors).
	Fertility float64
	// Ore is rock holding a mineable resource; Tree is a cell under a
	// tree (#778).
	Ore, Tree bool
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
	ModuleStorage  ModuleRole = "storage"
	ModuleWorkshop ModuleRole = "workshop"
	// ModuleReserve is sound ground held for growth.
	ModuleReserve ModuleRole = "reserve"
)

// LayoutEdgeMargin is how far from the map edge nothing is built: raiders
// and the map edge's fog make the band useless.
const LayoutEdgeMargin int32 = 10
