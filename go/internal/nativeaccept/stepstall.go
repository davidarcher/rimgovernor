package nativeaccept

import (
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// StepStallError is Run's fail-fast verdict when RunConfig.StepStall elapsed
// without any scheduler step admitting a clock window (issue #103). It names
// the family selection the service ran with and the last step failure the
// clock worker recorded, so a starved step budget reads as such instead of as
// twenty minutes of an unchanged EnsureFoodSupply timeline.
type StepStallError struct {
	Stall    time.Duration
	Families string
	// LastFailure is the last WARN or ERROR scheduler_step row of the
	// service's flight recorder, rendered by StepFailureText; empty when it
	// recorded none.
	LastFailure string
}

func (e *StepStallError) Error() string {
	var b strings.Builder
	fmt.Fprintf(&b, "no scheduler step admitted a clock window within %s of the watch starting (RIMGOVERNOR_ROUTINE_FAMILIES=%q)", e.Stall, e.Families)
	if e.LastFailure != "" {
		fmt.Fprintf(&b, ": %s", e.LastFailure)
	}
	b.WriteString("; the composed step is starved of its budget -- on a shared machine narrow -families to the family under test (e.g. field) or run without peer games")
	return b.String()
}

// lastStepFailure returns the service's most recent failed step from its
// flight recorder at path, or "" when it has none or cannot be read.
func lastStepFailure(path string) string {
	row, _, ok := lastFlightRow(path, 0, func(r FlightRow) bool {
		level, _ := r.Context["level"].(string)
		return bridge.IsSchedulerStepKind(r.Kind) && (level == "WARN" || level == "ERROR")
	})
	if !ok {
		return ""
	}
	return StepFailureText(row)
}

// StepFailureText renders a failed scheduler_step row as
// "<at> tick=<n> <LEVEL> <msg>" (the step message carries the error).
func StepFailureText(row FlightRow) string {
	level, _ := row.Context["level"].(string)
	at, _ := row.Context["at"].(string)
	msg, _ := row.Fields()["msg"].(string)
	parts := []string{}
	if at != "" {
		parts = append(parts, at)
	}
	if row.HasTick {
		parts = append(parts, fmt.Sprintf("tick=%d", row.Tick))
	}
	parts = append(parts, level, msg)
	return strings.Join(parts, " ")
}
