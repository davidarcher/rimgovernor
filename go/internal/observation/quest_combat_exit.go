package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// CombatSiteExit preserves the native exact exit cells from the same combat
// frame. No remote map selection or additional observation is performed.
func CombatSiteExit(read *bridge.WorldProgressionRead, mapID domain.MapID) domain.Fact[policy.CombatSiteExit] {
	sites, known := frameWorldSites(read).Value()
	if !known {
		return domain.Unknown[policy.CombatSiteExit]()
	}
	for _, site := range sites {
		mapped, mk := site.Map.Value()
		if !mk || mapped != mapID {
			continue
		}
		extraction, known := site.Extraction.Value()
		if !known {
			return domain.Unknown[policy.CombatSiteExit]()
		}
		return domain.Known(policy.CombatSiteExit{Crew: extraction.Crew, Cells: extraction.ExitCells})
	}
	return domain.Unknown[policy.CombatSiteExit]()
}
