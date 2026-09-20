package nativeaccept

import "path/filepath"

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
