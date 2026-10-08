package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func frameWorldSites(read *bridge.WorldProgressionRead) domain.Fact[[]policy.WorldSite] {
	if read == nil {
		return domain.Unknown[[]policy.WorldSite]()
	}
	rows := make([]policy.WorldSite, 0, len(read.Sites))
	for _, site := range read.Sites {
		row := policy.WorldSite{ID: site.GetId(), Def: site.GetDefName(), Label: site.GetLabel(), Tile: optional(site.Tile), Layer: optional(site.LayerId), State: site.GetState(), DistanceTiles: optional(site.DistanceTiles), Threat: optional(site.Threat), ThreatPoints: optional(site.ThreatPoints), Reachable: optional(site.Reachable), TravelTicks: optional(site.TravelTicks)}
		if site.MapId != nil {
			row.Map = domain.Known(domain.MapID(*site.MapId))
		}
		for _, id := range site.QuestIds {
			row.QuestIDs = append(row.QuestIDs, domain.QuestID(id))
		}
		for _, id := range site.RoutePawnIds {
			row.RoutePawnIDs = append(row.RoutePawnIDs, domain.PawnID(id))
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}

func questGravEngine(engine *o.QuestGravEngine) domain.Fact[policy.QuestGravEngine] {
	if engine == nil {
		return domain.Unknown[policy.QuestGravEngine]()
	}
	row := policy.QuestGravEngine{ID: engine.GetEngineId(), Map: domain.MapID(engine.GetMapId()), Spawned: optional(engine.Spawned), Inspected: optional(engine.Inspected)}
	if engine.Cell != nil {
		row.Cell = domain.Cell{X: engine.Cell.GetX(), Z: engine.Cell.GetZ()}
	}
	for _, id := range engine.EligiblePawnIds {
		row.EligiblePawns = append(row.EligiblePawns, domain.PawnID(id))
	}
	for _, id := range engine.InspectingPawnIds {
		row.InspectingPawnIDs = append(row.InspectingPawnIDs, domain.PawnID(id))
	}
	return domain.Known(row)
}
