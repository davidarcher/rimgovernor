package bridge_test

import (
	"encoding/json"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// Rows written through the real OTLP file sink come back as the rows a flight
// reader held: decisions with verdict, reason, target, dur_ms and attrs; events
// with msg and fields; numbers as float64; native_call replies as structure.
func TestReadOTLPTimelineRebuildsRows(t *testing.T) {
	dir := t.TempDir()
	ring := telemetrytest.WriteLogs(t, dir,
		telemetrytest.Row{
			Kind:    "dispatch",
			Context: map[string]any{"level": "WARN", "at": "2026-01-02T03:04:05.5Z", "tick": 77, "component": "routine"},
			Payload: map[string]any{"verdict": "refused", "reason": "no_room", "target": "build", "dur_ms": 1.5,
				"attrs": map[string]any{"receipt": "refused", "n": 3, "refused": []string{"a", "b"}, "tally": map[string]any{"x": 1}}},
		},
		telemetrytest.Row{
			Kind:    "native_call",
			Context: map[string]any{"component": "bridge"},
			Payload: map[string]any{"tool": "games_call_tool", "native_tool": "rimgovernor/clock_read_status", "ok": true,
				"result": json.RawMessage(`{"payload":"{\"status\":{\"actualPaused\":true}}"}`),
				"timing": map[string]any{"call_ms": 2.5, "total_ms": 3.0, "response_bytes": 10}},
		},
		telemetrytest.Row{
			Kind:    "placement_refused",
			Context: map[string]any{"component": "building-planner"},
			Payload: map[string]any{"msg": "furniture cell refused: Bed@1,2 blocked by Rock"},
		},
	)
	rows, err := bridge.ReadOTLPTimeline(ring)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 3 {
		t.Fatalf("rows = %d: %+v", len(rows), rows)
	}
	d := rows[0]
	if d.Kind != "dispatch" || d.Sequence != 1 || !d.HasSeq || d.Run != "test-run" || d.Context["level"] != "WARN" || d.Context["tick"] != 77.0 || d.Context["component"] != "routine" {
		t.Fatalf("dispatch row = %+v", d)
	}
	wantAttrs := map[string]any{"receipt": "refused", "n": 3.0, "refused": []any{"a", "b"}, "tally": map[string]any{"x": 1.0}}
	if d.Payload["verdict"] != "refused" || d.Payload["reason"] != "no_room" || d.Payload["target"] != "build" || d.Payload["dur_ms"] != 1.5 || !reflect.DeepEqual(d.Payload["attrs"], wantAttrs) {
		t.Fatalf("dispatch payload = %#v", d.Payload)
	}
	if !bridge.IsDecisionRow(d) || bridge.RowFields(d)["n"] != 3.0 {
		t.Fatalf("decision helpers disagree: %#v", bridge.RowFields(d))
	}
	if d.WallTime != 1767323045.5 {
		t.Fatalf("wall time = %v", d.WallTime)
	}

	call := rows[1]
	if call.Kind != "native_call" || bridge.NativeReplyFailed(call) {
		t.Fatalf("call row = %+v", call)
	}
	result, _ := call.Payload["result"].(map[string]any)
	if result["payload"] != `{"status":{"actualPaused":true}}` {
		t.Fatalf("result = %#v", call.Payload["result"])
	}
	phases := bridge.SummarizePhases(rows)
	if len(phases.Tools) != 1 || phases.Tools[0].Calls != 1 || phases.Tools[0].CallMs != 2.5 || phases.Tools[0].ResponseBytes != 10 || phases.Clock.PausedSamples != 1 {
		t.Fatalf("phases = %+v", phases)
	}

	if msg := rows[2].Payload["msg"]; msg != "furniture cell refused: Bed@1,2 blocked by Rock" || bridge.IsDecisionRow(rows[2]) {
		t.Fatalf("event row = %+v", rows[2])
	}

	if none, err := bridge.ReadOTLPTimeline(filepath.Join(dir, "missing", "logs.jsonl")); err != nil || len(none) != 0 {
		t.Fatalf("missing ring = %v, %v", none, err)
	}
}
