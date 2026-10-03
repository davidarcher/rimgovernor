package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func herdDefinition(name string, available bool, size domain.Fact[policy.Bounds]) observation.PlanningDefinition {
	return observation.PlanningDefinition{Name: name, Available: domain.Known(available), Size: size}
}

func TestHerdFurnitureFailsLoudlyWhenTheCatalogLacksADefinition(t *testing.T) {
	one := domain.Known(policy.Bounds{Width: 1, Height: 1})
	spot, bed := policy.AnimalSleepingSpotDefinition, policy.AnimalBedDefinition
	project := func(defs ...observation.PlanningDefinition) observation.ColonyProjection {
		return observation.ColonyProjection{Definitions: defs}
	}
	if _, _, err := herdFurniture(project(herdDefinition(spot, true, one))); err == nil {
		t.Fatal("a definition never read is an error")
	}
	if _, _, err := herdFurniture(project(herdDefinition(spot, true, one), herdDefinition(bed, false, domain.Unknown[policy.Bounds]()))); err == nil {
		t.Fatal("a definition the catalog lacks is an error")
	}
	if _, usable, err := herdFurniture(project(herdDefinition(spot, true, one), herdDefinition(bed, false, one))); err != nil || usable {
		t.Fatal("a definition awaiting research waits quietly", usable, err)
	}
	got, usable, err := herdFurniture(project(herdDefinition(spot, true, one), herdDefinition(bed, true, domain.Known(policy.Bounds{Width: 1, Height: 2}))))
	if err != nil || !usable || got.Spot.Size != (domain.Cell{X: 1, Z: 1}) || got.Bed.Size != (domain.Cell{X: 1, Z: 2}) {
		t.Fatal("both definitions read", got, usable, err)
	}
}
