package buildingruntime

import (
	"context"
	"os"

	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// recordedMirror is a new colony mirror whose tables are recorded into
// the serve's snapshot stream when recording is on (#795 step 4).
func recordedMirror() *mirror.Mirror {
	m := mirror.New()
	if dir := os.Getenv(snap.DirEnv); dir != "" {
		m.SetRecorder(snap.MirrorRecorder(dir))
	}
	return m
}

// recordStepRead appends a planner step's own colony read (planner is
// "building" or "bill") to the serve's snapshot stream for replay
// (#794) when snapshot recording is on; a failed write is logged.
func recordStepRead(planner string, goal policy.GoalID, current domain.GenerationSnapshot, reading observation.ColonyProjection) {
	dir := os.Getenv(snap.DirEnv)
	if dir == "" {
		return
	}
	if err := snap.RecordStep(dir, planner, goal, current, reading); err != nil {
		clockSchedulerLog("%s: step read snapshot not recorded: %v", goal, err)
	}
}

// recordPlannerStep starts recording one planner step's policy inputs for
// replay (#745, #746) when snapshot recording is on; the returned func
// writes them, logging a failed write rather than failing the step.
func recordPlannerStep(call context.Context, goal policy.GoalID, current domain.GenerationSnapshot, tick domain.Tick) (context.Context, func()) {
	call, finish := snap.StartPlanner(call, goal)
	return call, func() {
		if err := finish(current, tick); err != nil {
			clockSchedulerLog("%s: planner snapshot not recorded: %v", goal, err)
		}
	}
}
