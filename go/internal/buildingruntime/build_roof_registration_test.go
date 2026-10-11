package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A build-roof plan stalls silently unless routine execution dispatches the
// kind and the clock scheduler counts it as window work.
func TestBuildRoofKindIsRegisteredForRoutineExecutionAndClockWindow(t *testing.T) {
	t.Parallel()
	if !roundsExecutableKind(domain.BuildRoofAction) {
		t.Fatal("roundsExecutableKind omits build_roof")
	}
	roof, err := domain.NewBuildRoof([]domain.Cell{{X: 4, Z: 3}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildRoofAction("roof", roof)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("roof-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(spec, "roof")
	if err != nil {
		t.Fatal(err)
	}
	current := domain.GenerationSnapshot{Plan: spec.ID(), Revision: spec.Revision()}
	work, items, err := clockSchedulerWork(store.PlanState{Spec: spec, Progress: []domain.Progress{progress}}, current)
	if err != nil || !work || len(items) != 1 {
		t.Fatal("clock window omits build_roof", work, items, err)
	}
}
