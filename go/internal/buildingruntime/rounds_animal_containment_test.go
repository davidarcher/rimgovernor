package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func handlingPawn(available bool, priority int, disabled bool) policy.WorkPawn {
	return policy.WorkPawn{ID: "pawn-1", Available: domain.Known(available),
		Work: domain.Known([]policy.WorkPriority{{Work: "Handling", Priority: priority, Disabled: disabled}})}
}

func TestAnimalHandlerAvailableTrueOnlyWithEnabledPrioritizedHandling(t *testing.T) {
	if v := animalHandlerAvailable([]policy.WorkPawn{handlingPawn(true, 1, false)}); v != domain.Known(true) {
		t.Fatal(v)
	}
	if v := animalHandlerAvailable([]policy.WorkPawn{handlingPawn(true, 0, false)}); v != domain.Known(false) {
		t.Fatal(v)
	}
	if v := animalHandlerAvailable([]policy.WorkPawn{handlingPawn(true, 1, true)}); v != domain.Known(false) {
		t.Fatal(v)
	}
	if v := animalHandlerAvailable([]policy.WorkPawn{handlingPawn(false, 1, false)}); v != domain.Known(false) {
		t.Fatal(v)
	}
	if v := animalHandlerAvailable(nil); v != domain.Known(false) {
		t.Fatal(v)
	}
}

func TestAnimalHandlerAvailableUnknownOnUnresolvedFacts(t *testing.T) {
	if v := animalHandlerAvailable([]policy.WorkPawn{{ID: "pawn-1", Available: domain.Unknown[bool]()}}); v != domain.Unknown[bool]() {
		t.Fatal(v)
	}
	if v := animalHandlerAvailable([]policy.WorkPawn{{ID: "pawn-1", Available: domain.Known(true), Work: domain.Unknown[[]policy.WorkPriority]()}}); v != domain.Unknown[bool]() {
		t.Fatal(v)
	}
}

func TestAnimalContainmentStuffRequiresSharedOrBothAbsentMaterial(t *testing.T) {
	known := func(v string) observation.PlanningDefinition {
		return observation.PlanningDefinition{Stuffed: true, StuffOptions: madeOf(v)}
	}
	unknown := observation.PlanningDefinition{}
	if stuff, ok := animalContainmentStuff(known("WoodLog"), known("WoodLog")); !ok || stuff != "WoodLog" {
		t.Fatalf("stuff=%q ok=%v", stuff, ok)
	}
	if _, ok := animalContainmentStuff(known("WoodLog"), known("Steel")); ok {
		t.Fatal("mismatched material accepted")
	}
	if stuff, ok := animalContainmentStuff(unknown, unknown); !ok || stuff != "" {
		t.Fatalf("stuff=%q ok=%v", stuff, ok)
	}
	if _, ok := animalContainmentStuff(known("WoodLog"), unknown); ok {
		t.Fatal("partially known material accepted")
	}
}
