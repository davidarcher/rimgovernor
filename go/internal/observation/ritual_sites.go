package observation

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ritualSites are the finished buildings of the frame's building table whose
// ThingDef a held ritual's pattern requires (#1660): the cells the game
// offers the begin command at. The required defs are the catalog's ritual
// defs, never a list here. Unknown without the ideoligion or the building
// table.
func ritualSites(census *bridge.BuildingCensus, ideology domain.Fact[policy.Ideoligion]) domain.Fact[[]policy.RitualSite] {
	ideo, known := ideology.Value()
	if !known || census == nil {
		return domain.Unknown[[]policy.RitualSite]()
	}
	var wanted []string
	for _, held := range ideo.Facts.Rituals {
		wanted = append(wanted, ideo.Defs.Rituals[held.Pattern].RequiredBuildings...)
	}
	out := []policy.RitualSite{}
	if len(wanted) == 0 {
		return domain.Known(out)
	}
	for row := range census.Rows.Values() {
		def := row.GetBuilding().GetDefName()
		if row.GetStatus() != o.BuildingStatus_BUILDING_STATUS_BUILT || !slices.Contains(wanted, def) {
			continue
		}
		cell, ok := rowCell(row.GetBuilding().GetPosition()).Value()
		if !ok {
			return domain.Unknown[[]policy.RitualSite]()
		}
		out = append(out, policy.RitualSite{ID: row.GetBuilding().GetId(), Def: def, Cell: cell})
	}
	return domain.Known(out)
}
