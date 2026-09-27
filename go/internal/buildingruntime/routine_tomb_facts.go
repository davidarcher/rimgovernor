package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// wasteDefinitions are the definitions the waste steps read availability
// and stuff for.
var wasteDefinitions = []string{"Wall", "Door", policy.SarcophagusDefinition}

// tombStep is the projection's next tomb step (#832); none while the
// sarcophagus is unavailable or a fact is unknown.
func tombStep(facts observation.ColonyProjection) policy.TombStep {
	if owed, known := tombOwed(facts).Value(); !known || !owed {
		return policy.TombStep{}
	}
	plan, _ := facts.LayoutPlan.Value()
	rooms, _ := facts.Rooms.Value()
	waste, _ := facts.Facts.Waste.Value()
	built, _ := facts.Facts.CurrentConstruction.Value()
	return policy.NextTombStep(plan, rooms, waste, built.Buildings)
}

// tombOwed is the review's TombOwed fact for the projection.
func tombOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return policy.TombOwed(facts.DefinitionAvailable(policy.SarcophagusDefinition), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction)
}
