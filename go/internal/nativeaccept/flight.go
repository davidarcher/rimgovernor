package nativeaccept

import (
	"fmt"
	"path/filepath"
	"strings"
)

// FlightRecorderPath is where every launch has rimgovernor serve write its
// timeline under output (launchServe passes --flight-recorder); the
// runner's metrics block summarizes it (metrics.go).
func FlightRecorderPath(output string) string { return filepath.Join(output, "flight.jsonl") }

// ReadFlight reads every row of a service's flight recorder at path from
// its start (FlightTail follows appends only): what a case audits after
// the service stopped. A missing file is an empty recording.
func ReadFlight(path string) ([]FlightRow, error) {
	t := &FlightTail{path: path}
	return t.Next()
}

// FlightMessages are the msg payloads of every row of kind in the recorder at
// path, oldest first: the building planner's placement_refused and
// foreign_held rows carry what they report there (#2271, #2269).
func FlightMessages(path, kind string) ([]string, error) {
	rows, err := ReadFlight(path)
	if err != nil {
		return nil, err
	}
	var out []string
	for _, row := range rows {
		if row.Kind != kind {
			continue
		}
		if msg, ok := row.Payload["msg"].(string); ok {
			out = append(out, msg)
		}
	}
	return out, nil
}

// RefusalsNameBlockers returns every placement_refused message in the recorder
// at path and fails when one names no blocker: a refused furniture cell
// reports the thing the native preview found in the way (#2271).
func RefusalsNameBlockers(path string) ([]string, error) {
	refused, err := FlightMessages(path, "placement_refused")
	if err != nil {
		return nil, err
	}
	for _, msg := range refused {
		if _, blocker, ok := strings.Cut(msg, " blocked by "); !ok || blocker == "" || blocker == "unreported" {
			return refused, fmt.Errorf("a refused cell reports no blocker: %q", msg)
		}
	}
	return refused, nil
}
