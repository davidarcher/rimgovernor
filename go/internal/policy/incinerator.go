package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The incinerator (#1814, epic #1640): a walled, unroofed 3x3 room inside the
// waste yard (#2187), planned from the start in the outskirts cluster
// (growOutskirtsRooms). Rotten food, rotting animal corpses and worn gear haul
// into it from the dump (a higher priority zone over its floor) and it is
// burned whole. Its room is a plan room like the tomb's: the walls and door are
// non-flammable (observation FireproofStuff) so the fire stays inside, and the
// room has no roof so it does not heat up and kill the pawns who clean the ash.
// Unlike every other room it is permanent: clothing always wears and corpses
// always happen, so the plan never drops or moves it.

// PlannedIncinerator is the incinerator's plan role.
const PlannedIncinerator PlannedRole = "incinerator"

// IncineratorRooms are the plan's incinerator rooms, in plan order.
func (p LayoutPlan) IncineratorRooms() []PlannedRoom { return p.roomsOf(PlannedIncinerator) }

// incineratorSites is the incinerator zone once its walls stand: the whole
// interior, taking rotten and worn dump items at a priority above the Low
// dumps so they haul in from them.
func (r StorageRequest) incineratorSites() []StockpileSite {
	if r.Dumps == nil || r.Dumps.Incinerator == nil {
		return nil
	}
	cells := stockpileSorted(RectangleCells(r.Dumps.Incinerator.Interior))
	return []StockpileSite{{Role: domain.IncineratorRole, Room: cells, Filter: domain.IncineratorFilter(), Priority: domain.PreferredPriority, Keyed: true, Candidates: [][]domain.Cell{cells}}}
}
