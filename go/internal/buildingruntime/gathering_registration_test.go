package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A gathering plan stalls silently unless routine execution dispatches the
// kind and the clock scheduler counts it as window work.
func TestGatheringKindIsRegisteredForRoutineExecutionAndClockWindow(t *testing.T) {
	t.Parallel()
	if !roundsExecutableKind(domain.GatheringAction) {
		t.Fatal("roundsExecutableKind omits gathering")
	}
	gathering, err := domain.NewGathering("Party", "Human1")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewGatheringAction("gather", gathering)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("gather-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(spec, "gather")
	if err != nil {
		t.Fatal(err)
	}
	current := domain.GenerationSnapshot{Plan: spec.ID(), Revision: spec.Revision()}
	work, items, err := clockSchedulerWork(store.PlanState{Spec: spec, Progress: []domain.Progress{progress}}, current)
	if err != nil || !work || len(items) != 1 {
		t.Fatal("clock window omits gathering", work, items, err)
	}
}
