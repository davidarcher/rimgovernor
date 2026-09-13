package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func penShellPlan(t *testing.T, id domain.PlanID, originX, originZ int32, complete bool) store.PlanState {
	t.Helper()
	door := domain.Cell{X: originX + 3, Z: originZ}
	var actions []domain.Action
	i := 0
	for x := originX; x < originX+6; x++ {
		for z := originZ; z < originZ+6; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != door && (x != originX && x != originX+5 && z != originZ && z != originZ+5) {
				continue
			}
			def := "Fence"
			if cell == door {
				def = "FenceGate"
			}
			b, err := domain.NewBuilding(def, cell, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), b)
			if err != nil {
				t.Fatal(err)
			}
			actions = append(actions, a)
			i++
		}
	}
	spec, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	var progress []domain.Progress
	if complete {
		snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Direction: 1, Native: 1, Plan: id, Revision: 1}
		for _, a := range actions {
			progress = append(progress, completedProgress(t, spec, a.ID(), snapshot))
		}
	}
	return store.PlanState{Spec: spec, Progress: progress}
}

// completedProgress advances one action through the real Pending -> Prepared
// -> Dispatched -> Completed sequence, matching the exact transitions
// routine_shelter_test.go uses to fabricate an observed-complete shell.
func completedProgress(t *testing.T, spec domain.PlanSpec, action domain.ActionID, snapshot domain.GenerationSnapshot) domain.Progress {
	t.Helper()
	p, err := domain.NewProgress(spec, action)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Prepare(snapshot, 7)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(snapshot, 7)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Observe(domain.Observation{Action: action, Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

func penMarkerPlan(t *testing.T, id domain.PlanID, cell domain.Cell) store.PlanState {
	t.Helper()
	b, err := domain.NewBuilding("PenMarker", cell, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan(id, 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return store.PlanState{Spec: spec}
}

func TestAnimalContainmentPlanKindOfRecoversShellBoundingBox(t *testing.T) {
	plan := penShellPlan(t, "pen-shell-1", 10, 20, false)
	kind, room := animalContainmentPlanKindOf(plan.Spec)
	if kind != animalContainmentPlanShell || room != (policy.Rectangle{X: 10, Z: 20, Width: 6, Height: 6}) {
		t.Fatalf("kind=%v room=%+v", kind, room)
	}
}

func TestAnimalContainmentPlanKindOfRecognizesMarker(t *testing.T) {
	plan := penMarkerPlan(t, "pen-marker-1", domain.Cell{X: 5, Z: 5})
	kind, _ := animalContainmentPlanKindOf(plan.Spec)
	if kind != animalContainmentPlanMarker {
		t.Fatalf("kind=%v", kind)
	}
}

func TestAnimalContainmentPlanKindOfIgnoresUnrelatedPlans(t *testing.T) {
	b, err := domain.NewBuilding("SleepingSpot", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("other-0", b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("other", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	kind, _ := animalContainmentPlanKindOf(spec)
	if kind != animalContainmentPlanOther {
		t.Fatalf("kind=%v", kind)
	}
}

func TestAnimalContainmentPlanCompleteRequiresEveryActionCompleted(t *testing.T) {
	complete := penShellPlan(t, "pen-shell-2", 0, 0, true)
	if !animalContainmentPlanComplete(complete) {
		t.Fatal("fully completed shell plan reported incomplete")
	}
	pending := penShellPlan(t, "pen-shell-3", 0, 0, false)
	if animalContainmentPlanComplete(pending) {
		t.Fatal("plan with no progress reported complete")
	}
}

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
		return observation.PlanningDefinition{Stuff: domain.Known(v)}
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
