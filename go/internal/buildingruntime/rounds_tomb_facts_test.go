package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestSarcophagusWithoutStuffFallsBackToAGrave(t *testing.T) {
	facts := observation.ColonyProjection{Shapes: policy.PieceShapes{Furniture: policy.RoomFurniture{Sarcophagus: "Sarcophagus"}}, Definitions: []observation.PlanningDefinition{{Name: "Sarcophagus", Available: domain.Known(true), Stuffed: true}}}
	if v, known := sarcophagusAvailable(facts).Value(); !known || v {
		t.Fatalf("no stuff: %v %v", v, known)
	}
	facts.Definitions[0].Stuffed, facts.Definitions[0].StuffOptions = true, madeOf("BlocksGranite")
	if v, known := sarcophagusAvailable(facts).Value(); !known || !v {
		t.Fatalf("stuff: %v %v", v, known)
	}
	facts.Facts.Waste = domain.Known([]policy.WasteItem{{ID: "Corpse_1", State: policy.WasteExposed, CorpseOf: domain.CorpseColonist}})
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	if !tombsFull(policy.LayoutPlan{}, facts) {
		t.Fatal("a plan with no tomb owes one")
	}
}

// A restarted reviewer holds no refusal until its forced first survey
// replans; the answer then comes from that survey's tick alone (#857).
func TestTombGrowthRefusedAfterRestart(t *testing.T) {
	restarted := &Rounder{}
	if restarted.tombGrowthRefused(500000) {
		t.Fatal("no survey yet this process: the review replans first")
	}
	restarted.planChecked, restarted.planSurveyed = 500000, true
	if !restarted.tombGrowthRefused(500000) || !restarted.tombGrowthRefused(500000+layoutReplanEvery-1) {
		t.Fatal("a still-full plan after today's survey refuses another tomb")
	}
	if restarted.tombGrowthRefused(500000 + layoutReplanEvery) {
		t.Fatal("a day on the review replans again before a grave")
	}
	if restarted.tombGrowthRefused(400000) {
		t.Fatal("a rewind past the survey replans again")
	}
}
