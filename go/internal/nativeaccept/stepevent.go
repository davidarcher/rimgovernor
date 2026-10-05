package nativeaccept

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// SchedulerStep is the service's latest scheduler_step row (the clock
// worker writes one per change of step outcome): the row as the flight
// recorder wrote it and the isolated planner failures it carried.
type SchedulerStep struct {
	// Line is the row's JSON line, distinct per row (it carries the
	// sequence), so a caller can tell a new step from a re-read one.
	Line            string
	PlannerFailures string
}

// Refused reports whether the step's isolated planner failures include a
// native refusal (bridge.ErrRefused wraps every one as "bridge read
// refused: <tool>"; planner-level refusals name themselves "refused" too).
// A step whose planners all succeeded, or failed on transport, is not.
func (s SchedulerStep) Refused() bool {
	return strings.Contains(s.PlannerFailures, "refused")
}

// schedulerStepTail bounds how much of a growing flight recorder
// LastSchedulerStepFile reads per call: the latest step row is near the
// end, and a poll must not re-read an hour of rows.
const schedulerStepTail = 256 * 1024

// LastSchedulerStepFile reads the last scheduler_step row from the tail of
// the flight recorder at path; false when it has none yet or cannot be read.
func LastSchedulerStepFile(path string) (SchedulerStep, bool) {
	row, line, ok := lastFlightRow(path, schedulerStepTail, func(r FlightRow) bool { return bridge.IsSchedulerStepKind(r.Kind) })
	if !ok {
		return SchedulerStep{}, false
	}
	return SchedulerStep{Line: line, PlannerFailures: plannerFailures(row)}, true
}

// plannerFailures renders the row's planner_failures list as "[a; b]" ("[]"
// when empty).
func plannerFailures(row FlightRow) string {
	list, _ := row.Fields()["planner_failures"].([]any)
	parts := make([]string, 0, len(list))
	for _, v := range list {
		parts = append(parts, fmt.Sprint(v))
	}
	return "[" + strings.Join(parts, "; ") + "]"
}
