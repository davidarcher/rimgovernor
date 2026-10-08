package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func validQuestIdentities(ids []string) bool {
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || seen[id] {
			return false
		}
		seen[id] = true
	}
	return true
}
func validNonnegative(v float64) bool { return v >= 0 && !math.IsNaN(v) && !math.IsInf(v, 0) }

type CaravanAssemblyFact struct {
	ID      string
	MapID   int32
	PawnIDs []string
}

func validatedCaravanAssemblies(rows []*o.CaravanAssembly) ([]CaravanAssemblyFact, error) {
	out := make([]CaravanAssemblyFact, 0, len(rows))
	seen := map[string]bool{}
	for _, row := range rows {
		if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] || row.MapId == nil || row.GetMapId() < 0 {
			return nil, contract("invalid caravan assembly")
		}
		seen[row.GetId()] = true
		fact := CaravanAssemblyFact{ID: row.GetId(), MapID: row.GetMapId()}
		for _, p := range row.Pawns {
			if p == nil {
				return nil, contract("invalid caravan assembly pawn")
			}
			fact.PawnIDs = append(fact.PawnIDs, p.GetId())
		}
		if !validQuestIdentities(fact.PawnIDs) {
			return nil, contract("invalid caravan assembly crew")
		}
		out = append(out, fact)
	}
	return out, nil
}

func validatedWorldSites(rows []*o.WorldSite) ([]*o.WorldSite, error) {
	out := make([]*o.WorldSite, 0, len(rows))
	seen := map[string]bool{}
	for _, row := range rows {
		if row == nil || validID(row.GetId()) != nil || row.GetDefName() == "" || seen[row.GetId()] || row.State == nil || row.GetState() < o.WorldSiteState_WORLD_SITE_STATE_UNKNOWN || row.GetState() > o.WorldSiteState_WORLD_SITE_STATE_DESTROYED {
			return nil, contract("invalid world site identity or state")
		}
		seen[row.GetId()] = true
		if row.GetTile() < 0 || row.GetLayerId() < 0 || row.GetMapId() < 0 || row.GetState() == o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED && row.MapId == nil {
			return nil, contract("invalid world site location")
		}
		if row.DistanceTiles != nil && !validNonnegative(row.GetDistanceTiles()) || row.ThreatPoints != nil && !validNonnegative(row.GetThreatPoints()) || row.GetTravelTicks() < 0 {
			return nil, contract("invalid world site estimate")
		}
		if !validQuestIdentities(row.QuestIds) || !validQuestIdentities(row.RoutePawnIds) {
			return nil, contract("invalid world site quest or route crew")
		}
		if row.TravelTicks != nil && (row.Reachable == nil || !row.GetReachable() || len(row.RoutePawnIds) == 0) {
			return nil, contract("world site estimate lacks reachable crew")
		}
		if err := validSiteExtensions(row); err != nil {
			return nil, err
		}
		out = append(out, proto.Clone(row).(*o.WorldSite))
	}
	return out, nil
}

func validatedQuestSurvey(row *o.QuestSurveyScanner) (*o.QuestSurveyScanner, error) {
	if row == nil {
		return nil, nil
	}
	if validID(row.GetSiteId()) != nil || row.ScannerId != nil && validID(row.GetScannerId()) != nil || row.GetDurationTicks() < 0 || row.GetEndTick() < 0 || row.GetRaidTick() < 0 {
		return nil, contract("invalid quest survey scanner")
	}
	if row.GetAlive() && row.ScannerId == nil {
		return nil, contract("live quest scanner lacks identity")
	}
	return proto.Clone(row).(*o.QuestSurveyScanner), nil
}

func validSiteExtensions(row *o.WorldSite) error {
	miningIDs := []string{}
	for _, m := range row.MiningTargets {
		if m == nil || m.GetDef() == "" || m.MapId == nil || m.GetMapId() < 0 || !monumentCellKnown(m.Cell) || m.Cell.GetX() < 0 || m.Cell.GetZ() < 0 {
			return contract("invalid site mining target")
		}
		miningIDs = append(miningIDs, m.GetId())
	}
	if !validQuestIdentities(miningIDs) {
		return contract("invalid site mining identity")
	}
	if s := row.Security; s != nil {
		if s.InitialPoints != nil && !validNonnegative(s.GetInitialPoints()) || s.PendingRaidPoints != nil && !validNonnegative(s.GetPendingRaidPoints()) || s.GetTrapCount() < 0 || s.GetDetectionTicksLeft() < 0 || s.GetRaidsSent() < 0 {
			return contract("invalid site security")
		}
	}
	if p := row.PeaceTalks; p != nil {
		if validID(p.GetFactionId()) != nil || p.GetCurrentGoodwill() < -100 || p.GetCurrentGoodwill() > 100 || p.GetWorstGoodwillLoss() < 0 || p.GetBestGoodwillGain() < 0 {
			return contract("invalid site diplomacy")
		}
	}
	if e := row.Extraction; e != nil {
		if row.GetState() != o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED || !validQuestIdentities(e.CrewIds) {
			return contract("invalid site extraction crew")
		}
		for _, v := range []*float64{e.CarryCapacity, e.CarriedMass, e.InventoryFoodDays, e.CrewNutritionPerDay} {
			if v != nil && !validNonnegative(*v) {
				return contract("invalid site extraction estimate")
			}
		}
		ids := []string{}
		for _, c := range e.Cargo {
			if c == nil || c.GetDef() == "" || c.Count == nil || c.GetCount() <= 0 {
				return contract("invalid site extraction cargo")
			}
			ids = append(ids, c.GetId())
			for _, v := range []*float64{c.UnitMass, c.MarketValue, c.Nutrition} {
				if v != nil && !validNonnegative(*v) {
					return contract("invalid site cargo estimate")
				}
			}
		}
		if !validQuestIdentities(ids) {
			return contract("invalid site cargo identity")
		}
		maps := map[int32]bool{}
		cells := map[[2]int32]bool{}
		for _, c := range e.ExitCells {
			if !monumentCellKnown(c) || c.GetX() < 0 || c.GetZ() < 0 || cells[[2]int32{c.GetX(), c.GetZ()}] {
				return contract("invalid site exit cell")
			}
			cells[[2]int32{c.GetX(), c.GetZ()}] = true
		}
		for _, r := range e.HomeRoutes {
			if r == nil || r.MapId == nil || r.Tile == nil || r.GetMapId() < 0 || r.GetTile() < 0 || maps[r.GetMapId()] || r.GetTravelTicks() < 0 || r.TravelTicks != nil && !r.GetReachable() {
				return contract("invalid site home route")
			}
			maps[r.GetMapId()] = true
		}
	}
	return nil
}

func validatedQuestGravEngine(row *o.QuestGravEngine) (*o.QuestGravEngine, error) {
	if row == nil {
		return nil, nil
	}
	if validID(row.GetEngineId()) != nil || row.GetMapId() < 0 {
		return nil, contract("invalid quest grav engine")
	}
	if row.GetSpawned() && (row.MapId == nil || !monumentCellKnown(row.Cell) || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0) {
		return nil, contract("spawned quest grav engine lacks location")
	}
	if !validQuestIdentities(row.EligiblePawnIds) || !validQuestIdentities(row.InspectingPawnIds) {
		return nil, contract("invalid quest grav engine worker")
	}
	return proto.Clone(row).(*o.QuestGravEngine), nil
}
