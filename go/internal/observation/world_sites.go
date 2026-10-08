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
		if s := site.Security; s != nil {
			row.Security = domain.Known(policy.SiteSecurity{Known: optional(s.Known), InitialPoints: optional(s.InitialPoints), PendingRaidPoints: optional(s.PendingRaidPoints), ActiveThreat: optional(s.ActiveThreat), DormantThreat: optional(s.DormantThreat), TrapCount: optional(s.TrapCount), DetectionActive: optional(s.DetectionActive), DetectionTicksLeft: optional(s.DetectionTicksLeft), RaidsSent: optional(s.RaidsSent)})
		}
		if p := site.PeaceTalks; p != nil {
			row.PeaceTalks = domain.Known(policy.PeaceTalksRisk{WorstGoodwillLoss: optional(p.WorstGoodwillLoss), BestGoodwillGain: optional(p.BestGoodwillGain), IdeologyActive: optional(p.IdeologyActive)})
		}
		if e := site.Extraction; e != nil {
			ex := policy.SiteExtraction{CanReform: optional(e.CanReform), CarryCapacity: optional(e.CarryCapacity), CarriedMass: optional(e.CarriedMass), InventoryFoodDays: optional(e.InventoryFoodDays), CrewNutritionPerDay: optional(e.CrewNutritionPerDay)}
			for _, id := range e.CrewIds {
				ex.Crew = append(ex.Crew, domain.PawnID(id))
			}
			for _, c := range e.Cargo {
				ex.Cargo = append(ex.Cargo, policy.SiteCargo{ID: c.GetId(), Def: c.GetDef(), Count: optional(c.Count), UnitMass: optional(c.UnitMass), MarketValue: optional(c.MarketValue), Nutrition: optional(c.Nutrition), Held: optional(c.Held)})
			}
			for _, r := range e.HomeRoutes {
				ex.HomeRoutes = append(ex.HomeRoutes, policy.SiteHomeRoute{Map: domain.MapID(r.GetMapId()), Tile: r.GetTile(), Reachable: optional(r.Reachable), TravelTicks: optional(r.TravelTicks)})
			}
			for _, c := range e.ExitCells {
				ex.ExitCells = append(ex.ExitCells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
			}
			row.Extraction = domain.Known(ex)
		}
		for _, m := range site.MiningTargets {
			row.MiningTargets = append(row.MiningTargets, policy.SiteMiningTarget{ID: m.GetId(), Def: m.GetDef(), Map: domain.MapID(m.GetMapId()), Cell: domain.Cell{X: m.Cell.GetX(), Z: m.Cell.GetZ()}, Designated: optional(m.Designated)})
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}

func questSurveyScanner(scanner *o.QuestSurveyScanner) domain.Fact[policy.QuestSurveyScanner] {
	if scanner == nil {
		return domain.Unknown[policy.QuestSurveyScanner]()
	}
	return domain.Known(policy.QuestSurveyScanner{SiteID: scanner.GetSiteId(), ScannerID: scanner.GetScannerId(), DurationTicks: optional(scanner.DurationTicks), EndTick: optional(scanner.EndTick), RaidTick: optional(scanner.RaidTick), Alive: optional(scanner.Alive), Complete: optional(scanner.Complete)})
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
