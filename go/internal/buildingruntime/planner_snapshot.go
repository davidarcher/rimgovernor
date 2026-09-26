package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

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
