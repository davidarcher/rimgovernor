package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestSarcophagusWithoutStuffFallsBackToAGrave(t *testing.T) {
	facts := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{{Name: policy.SarcophagusDefinition, Available: domain.Known(true), Stuff: domain.Known("")}}}
	if v, known := sarcophagusAvailable(facts).Value(); !known || v {
		t.Fatalf("no stuff: %v %v", v, known)
	}
	facts.Definitions[0].Stuff = domain.Known("BlocksGranite")
	if v, known := sarcophagusAvailable(facts).Value(); !known || !v {
		t.Fatalf("stuff: %v %v", v, known)
	}
	facts.Facts.Waste = domain.Known([]policy.WasteItem{{ID: "Corpse_1", State: policy.WasteExposed, CorpseOf: domain.CorpseColonist}})
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	if !tombsFull(policy.LayoutPlan{}, facts) {
		t.Fatal("a plan with no tomb owes one")
	}
}
