package nativeaccept

import (
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// FlightRecorderPath is where a harness that opts in (-flight-recorder)
// has rimgovernor serve write its native timeline under output.
func FlightRecorderPath(output string) string { return filepath.Join(output, "flight.jsonl") }

// FlightRecorderArgs are the serve flags that enable the recorder, or none.
func FlightRecorderArgs(output string, enabled bool) []string {
	if !enabled {
		return nil
	}
	return []string{"--flight-recorder", FlightRecorderPath(output)}
}

// ReportPhases summarizes the recorded timeline into report["phases"]
// (bridge.PhaseSummary: reads/step, step-cache and cross-step parent hits,
// per-tool phases) once the service has exited. Without a recording it
// leaves the report alone; a malformed one is reported, not fatal.
func ReportPhases(report Report, output string, enabled bool) {
	if !enabled {
		return
	}
	rows, err := bridge.ReadTimeline(FlightRecorderPath(output))
	if err != nil {
		report["phases_error"] = err.Error()
		return
	}
	report["phases"] = bridge.SummarizePhases(rows)
}
