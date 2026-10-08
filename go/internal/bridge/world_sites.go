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
		out = append(out, proto.Clone(row).(*o.WorldSite))
	}
	return out, nil
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
