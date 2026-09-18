package nativeaccept

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"strings"
	"time"
)

// StepStallError is Run's fail-fast verdict when RunConfig.StepStall elapsed
// without any scheduler step admitting a clock window (issue #103). It names
// the family selection the service ran with and the last step failure the
// clock worker logged, so a starved step budget reads as such instead of as
// twenty minutes of an unchanged EnsureFoodSupply timeline.
type StepStallError struct {
	Stall    time.Duration
	Families string
	// LastFailure is the last "[clock-worker] step failed:" line from the
	// service's stderr, empty when it logged none.
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

func stepStall(stall time.Duration, families, lastFailure string) error {
	return &StepStallError{Stall: stall, Families: families, LastFailure: lastFailure}
}

// stepFailurePrefix is the unconditional line ClockWorker.stepLoop writes
// when a step's error changes (buildingruntime/clock_worker.go).
const stepFailurePrefix = "[clock-worker] step failed:"

// lastStepFailure returns the service's most recent step failure line from
// its stderr log, or "" when the log has none or cannot be read.
func lastStepFailure(path string) string {
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	return LastStepFailure(f)
}

// LastStepFailure scans a service stderr stream for its last step failure.
func LastStepFailure(r io.Reader) string {
	var last string
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		if line := strings.TrimSpace(scanner.Text()); strings.HasPrefix(line, stepFailurePrefix) {
			last = line
		}
	}
	return last
}
