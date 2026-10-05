package buildingruntime

import (
	"context"
	"os"

	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// recordTables records the store's tables into the serve's snapshot
// stream when recording is on (#795 step 4).
func recordTables(store *facts.Store) {
	if dir := os.Getenv(snap.DirEnv); dir != "" {
		record := snap.MirrorRecorder(dir)
		store.SetRecorder(func(p facts.Published) { snap.Later(func() { record(p) }) })
	}
}

// recordStepRead appends a planner step's own colony read (planner is
// "building", "bill", "hospital" or "deepdrill") to the serve's snapshot stream for replay
// (#794) when snapshot recording is on; a failed write is logged.
func recordStepRead(planner string, goal policy.ConcernID, current domain.GenerationSnapshot, reading observation.ColonyProjection) {
	dir := os.Getenv(snap.DirEnv)
	if dir == "" {
		return
	}
	snap.Later(func() {
		_ = snap.RecordStep(dir, planner, goal, current, reading)
	})
}

// recordPlannerStep starts recording one planner step's policy inputs for
// replay (#745, #746) when snapshot recording is on; the returned func
// writes them; a failed write is dropped rather than failing the step.
func recordPlannerStep(call context.Context, goal policy.ConcernID, current domain.GenerationSnapshot, tick domain.Tick) (context.Context, func()) {
	call, finish := snap.StartPlanner(call, goal)
	return call, func() {
		_ = finish(current, tick)
	}
}
