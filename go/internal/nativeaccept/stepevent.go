package nativeaccept

import (
	"bufio"
	"io"
	"os"
	"strconv"
	"strings"
)

// SchedulerStep is the service's latest scheduler_step event as its stderr
// shows it (clock_worker.go's clockWorkerStepEvent, one line per change of
// step outcome): the whole stamped line and the isolated planner failures
// it carried, rendered as the log rendered them.
type SchedulerStep struct {
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

const (
	schedulerStepMark    = "[clock-worker] step "
	plannerFailuresField = " planner_failures="
	// schedulerStepTail bounds how much of a growing stderr log
	// LastSchedulerStepFile reads per call: the latest step line is near
	// the end, and a poll must not re-read an hour of service output.
	schedulerStepTail = 256 * 1024
)

// LastSchedulerStep scans a service stderr stream for its last
// scheduler_step line ("step done" or "step failed").
func LastSchedulerStep(r io.Reader) (SchedulerStep, bool) {
	var last string
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	for scanner.Scan() {
		if line := strings.TrimSpace(scanner.Text()); strings.Contains(line, schedulerStepMark) {
			last = line
		}
	}
	if last == "" {
		return SchedulerStep{}, false
	}
	return SchedulerStep{Line: last, PlannerFailures: plannerFailures(last)}, true
}

// LastSchedulerStepFile is LastSchedulerStep over the tail of the log at
// path; false when the log has no step line yet or cannot be read.
func LastSchedulerStepFile(path string) (SchedulerStep, bool) {
	f, err := os.Open(path)
	if err != nil {
		return SchedulerStep{}, false
	}
	defer f.Close()
	if info, err := f.Stat(); err == nil && info.Size() > schedulerStepTail {
		if _, err := f.Seek(info.Size()-schedulerStepTail, io.SeekStart); err != nil {
			return SchedulerStep{}, false
		}
	}
	return LastSchedulerStep(f)
}

// plannerFailures extracts the planner_failures attribute's rendered value
// from a step line: bare when it held no space (the empty list "[]"),
// otherwise the Go-quoted string telemetry's handler wrote.
func plannerFailures(line string) string {
	i := strings.Index(line, plannerFailuresField)
	if i < 0 {
		return ""
	}
	rest := line[i+len(plannerFailuresField):]
	if strings.HasPrefix(rest, `"`) {
		if quoted, err := strconv.QuotedPrefix(rest); err == nil {
			if value, err := strconv.Unquote(quoted); err == nil {
				return value
			}
		}
		return rest
	}
	if end := strings.IndexByte(rest, ' '); end >= 0 {
		rest = rest[:end]
	}
	return rest
}
