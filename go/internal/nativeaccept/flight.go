package nativeaccept

import "path/filepath"

// FlightRecorderPath is where every launch has rimgovernor serve write its
// timeline under output (launchServe passes --flight-recorder); the
// runner's metrics block summarizes it (metrics.go).
func FlightRecorderPath(output string) string { return filepath.Join(output, "flight.jsonl") }
