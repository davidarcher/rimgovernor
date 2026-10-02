package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateColonyBlight checks the blighted-plant census (#245): at most 64
// distinct plants on this map with a cut snapshot read under this context,
// and no rows at all under a blighted_plants issue.
func validateColonyBlight(v *o.ColonyFactsSnapshot) error {
	seen := map[string]bool{}
	for _, row := range v.BlightedPlants {
		plant := row.GetPlant()
		if !validRef(plant) || seen[plant.GetId()] || !refSnapshot(row.PlantSnapshot, plant, v.Context) || row.Designated == nil || !optionalRef(row.Zone) || row.Growth != nil && (!combatNumber(row.Growth, true) || row.GetGrowth() > 1) {
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
