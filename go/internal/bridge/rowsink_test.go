package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// rowSink installs the real OTLP file sink for the test and returns a function
// that flushes it and reads back every row written so far, as a reader of the
// service's log ring would. Tests using it must not run in parallel.
func rowSink(t *testing.T) func() []TimelineRecord {
	t.Helper()
	flush := telemetrytest.Sink(t)
	return func() []TimelineRecord {
		t.Helper()
		rows, err := ReadOTLPTimeline(flush())
		if err != nil {
			t.Fatal(err)
		}
		return rows
	}
}
