package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyBlight decodes the colony read's blighted_plants census (#245) into
// the rows BlightDeficit/SelectBlightCuts work from. A blighted_plants read
// issue withholds the whole census (unknown, never "no blight"); an empty
// list without one is the known, settled state.
func colonyBlight(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.BlightedPlant] {
	if hasIssue(v.Issues, "blighted_plants") {
		return domain.Unknown[[]policy.BlightedPlant]()
	}
	plants := make([]policy.BlightedPlant, 0, len(v.BlightedPlants))
	for _, row := range v.BlightedPlants {
		plant := row.GetPlant()
		position := plant.GetPosition()
		if plant.GetId() == "" || position == nil || position.X == nil || position.Z == nil {
			continue
		}
		plants = append(plants, policy.BlightedPlant{ID: plant.GetId(), Definition: plant.GetDefName(),
			Cell: domain.Cell{X: position.GetX(), Z: position.GetZ()}, Zone: row.GetZoneId(),
			Designated: row.GetDesignated(), Token: plant.GetSnapshot().GetToken()})
	}
	return domain.Known(plants)
}
