package nativeaccept

import (
	"fmt"
	"path/filepath"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/otel"
)

// A service's evidence is its OTLP/JSON file sink (docs/developers/contracts/otel-core.md):
// rimgovernor serve writes traces.jsonl, logs.jsonl and metrics.jsonl under
// the --otlp-dir directory. Decisions, events and the bridge's native rows
// are the log records; the case oracles read them as rows
// (bridge.RecordFromLog).

// OTLPDir is the --otlp-dir argument of a service whose evidence lives under
// dir.
func OTLPDir(dir string) string { return filepath.Join(dir, "otlp") }

// LogsPath is the log ring of the service launched with --otlp-dir otlpDir:
// otlpDir/logs.jsonl.
func LogsPath(otlpDir string) string { return filepath.Join(otlpDir, otel.LogsFile) }

// ServiceLogsPath is the log ring of the first service launch under output
// (launchServe passes --otlp-dir OTLPDir(output)); the runner's metrics block
// summarizes it (metrics.go).
func ServiceLogsPath(output string) string { return LogsPath(OTLPDir(output)) }

// ReadFlight reads every row of a service's log ring at path from its start,
// across the ring's rotated segments (FlightTail follows appends only): what
// a case audits after the service stopped. A missing ring is an empty
// recording.
func ReadFlight(path string) ([]FlightRow, error) {
	records, err := bridge.ReadOTLPTimeline(path)
	if err != nil {
		return nil, err
	}
	rows := make([]FlightRow, 0, len(records))
	for _, rec := range records {
		row := FlightRow{Sequence: rec.Sequence, Kind: rec.Kind, Context: rec.Context, Payload: rec.Payload}
		if tick, ok := row.Context["tick"].(float64); ok {
			row.Tick, row.HasTick = int64(tick), true
		}
		rows = append(rows, row)
	}
	return rows, nil
}

// FlightMessages are the msg payloads of every row of kind in the log ring at
// path, oldest first: the building planner's placement_refused and
// foreign_held rows carry what they report there.
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

// RefusalsNameBlockers returns every placement_refused message in the log ring
// at path and fails when one names no blocker: a refused furniture cell
// reports the thing the native preview found in the way.
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
