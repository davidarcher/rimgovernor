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

// A live colony built its fence ring, then the development review refused
// the priority-3 goal for Construction labor; the marker step was gated with
// the shell and the ring never became a pen.
func TestAnimalContainmentDevelopmentGatesOnlyTheShell(t *testing.T) {
	if !animalContainmentDevelopmentGated(3, false, policy.ContainmentBuildShell) {
		t.Fatal("unselected priority-3 shell must wait for development")
	}
	if animalContainmentDevelopmentGated(3, false, policy.ContainmentPlaceMarker) {
		t.Fatal("a completed shell's marker must not wait for development")
	}
	if animalContainmentDevelopmentGated(3, true, policy.ContainmentBuildShell) || animalContainmentDevelopmentGated(2, false, policy.ContainmentBuildShell) {
		t.Fatal("selected or higher-priority goals build without the gate")
	}
}
