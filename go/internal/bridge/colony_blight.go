package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// validateColonyBlight checks the blighted-plant census (#245): at most 64
// distinct plants on this map with a cut snapshot read under this context,
// and no rows at all under a blighted_plants issue.
func validateColonyBlight(v *o.ColonyFactsSnapshot) error {
	if len(v.BlightedPlants) > 64 {
		return contract("blighted plant census exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.BlightedPlants {
		plant := row.GetPlant()
		if plant == nil || validID(plant.GetId()) != nil || seen[plant.GetId()] || validID(plant.GetDefName()) != nil || plant.MapId == nil || plant.GetMapId() != v.Context.Identity.GetMapId() || !colonyCell(plant.Position, v.MapSize) || plant.Snapshot == nil || plant.Snapshot.GetEntityId() != plant.GetId() || validID(plant.Snapshot.GetToken()) != nil || !proto.Equal(plant.Snapshot.Context, v.Context) || row.Designated == nil || row.ZoneId != nil && validID(row.GetZoneId()) != nil || row.Growth != nil && (!combatNumber(row.Growth, true) || row.GetGrowth() > 1) {
			return contract("invalid blighted plant")
		}
		seen[plant.GetId()] = true
	}
	for _, issue := range v.Issues {
		if issue.GetField() == "blighted_plants" && len(v.BlightedPlants) > 0 {
			return contract("unavailable blight census contains plants")
		}
	}
	return nil
}
