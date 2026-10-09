package nativeaccept

import (
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// StepStallError is Run's fail-fast verdict when RunConfig.StepStall elapsed
// without any scheduler step admitting a clock window. It names
// the family selection the service ran with and the last step failure the
// clock worker recorded, so a starved step budget reads as such instead of as
// twenty minutes of an unchanged EnsureFoodSupply timeline.
type StepStallError struct {
	Stall    time.Duration
	Families string
	// LastFailure is the last WARN or ERROR worker-step planner_step row of the
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
		return bridge.IsWorkerStep(r.Record()) && (level == "WARN" || level == "ERROR")
	})
	if !ok {
		return ""
	}
	return StepFailureText(row)
}

// StepFailureText renders a failed worker-step row as
// "<at> tick=<n> <level> <verdict> <reason>: <error>" (the row's error attr
// and isolated planner failures carry the text).
func StepFailureText(row FlightRow) string {
	level, _ := row.Context["level"].(string)
	at, _ := row.Context["at"].(string)
	msg := stepFailureWords(row)
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

// stepFailureWords is the worker-step row's "<verdict> <reason>" followed by
// its error and the isolated planner failures when it carries them.
func stepFailureWords(row FlightRow) string {
	f := row.Fields()
	words := []string{}
	for _, key := range []string{"verdict", "reason"} {
		if s, _ := f[key].(string); s != "" {
			words = append(words, s)
		}
	}
	text := strings.Join(words, " ")
	if e, _ := f["error"].(string); e != "" {
		text += ": " + e
	}
	if failures := plannerFailures(row); failures != "[]" {
		text += " " + failures
	}
	return text
}
