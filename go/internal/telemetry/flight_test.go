package telemetry_test

import (
	"io"
	"log/slog"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// A kinded record is one flight row in sequence with the bridge's own rows,
// readable by the timeline reader like any other.
func TestKindedRecordsAreTimelineRows(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	recorder, err := bridge.NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	defer recorder.Close()
	logger := telemetry.New(io.Discard, slog.LevelInfo, recorder)
	telemetry.ObserveTick(77)
	if _, err = recorder.Event("native_request", nil, true, map[string]any{"tool": "x"}); err != nil {
		t.Fatal(err)
	}
	logger.Info("step done", telemetry.KindKey, "scheduler_step", telemetry.ComponentKey, "clock-worker", "planner_failures", []string{"Fields: x"}, "admitted", true)
	logger.Info("no kind, no row")
	if _, err = recorder.Event("native_response", nil, false, nil); err != nil {
		t.Fatal(err)
	}
	rows, err := bridge.ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	var kinds []string
	for _, row := range rows {
		kinds = append(kinds, row.Kind)
	}
	if len(rows) != 4 || kinds[1] != "native_request" || kinds[2] != "scheduler_step" || kinds[3] != "native_response" {
		t.Fatalf("kinds in sequence: %v", kinds)
	}
	for i := 1; i < len(rows); i++ {
		if rows[i].Sequence != rows[i-1].Sequence+1 {
			t.Fatalf("sequence gap at %d: %+v", i, rows)
		}
	}
	step := rows[2]
	if step.Context["tick"] != float64(77) || step.Context["component"] != "clock-worker" || step.Payload["msg"] != "step done" || step.Payload["admitted"] != true {
		t.Fatalf("row: %+v %+v", step.Context, step.Payload)
	}
	if failures, _ := step.Payload["planner_failures"].([]any); len(failures) != 1 || failures[0] != "Fields: x" {
		t.Fatalf("string slices encode as JSON arrays: %+v", step.Payload["planner_failures"])
	}
}
