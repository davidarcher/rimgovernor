package telemetry_test

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

func newRecorder(t *testing.T, opts ...bridge.FlightRecorderOption) (string, *bridge.FlightRecorder) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	recorder, err := bridge.NewFlightRecorder(path, opts...)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { recorder.Close() })
	return path, recorder
}

// A decision row round-trips through the timeline reader with the fixed
// payload shape and the full v2 context, under the ctx's trace.
func TestDecisionRowRoundTrip(t *testing.T) {
	path, recorder := newRecorder(t)
	logger := telemetry.New(io.Discard, slog.LevelInfo, recorder)
	telemetry.ObserveTick(1234)
	ctx, trace := telemetry.EnsureTrace(context.Background())
	telemetry.Decision{
		Kind: "admission", Component: "clock-scheduler", Level: slog.LevelWarn,
		Verdict: "refused", Reason: "critical_wave_budget", Target: "window",
		Dur:   1500 * time.Microsecond,
		Attrs: map[string]any{"err": errors.New("held"), "wait": 2 * time.Second, "n": 3},
	}.LogTo(ctx, logger)
	rows, err := bridge.ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	row := rows[len(rows)-1]
	if row.Kind != "admission" {
		t.Fatalf("kind %q", row.Kind)
	}
	for key, want := range map[string]any{"verdict": "refused", "reason": "critical_wave_budget", "target": "window", "dur_ms": 1.5} {
		if row.Payload[key] != want {
			t.Fatalf("payload %s = %v, want %v: %+v", key, row.Payload[key], want, row.Payload)
		}
	}
	if _, free := row.Payload["msg"]; free {
		t.Fatalf("a decision row carries no free text: %+v", row.Payload)
	}
	attrs, _ := row.Payload["attrs"].(map[string]any)
	if attrs["err"] != "held" || attrs["wait"] != 2000.0 || attrs["n"] != 3.0 {
		t.Fatalf("attrs: %+v", attrs)
	}
	c := row.Context
	if c["level"] != "WARN" || c["component"] != "clock-scheduler" || c["tick"] != 1234.0 || c["trace_id"] != trace.TraceID || c["span_id"] != trace.SpanID || c["at"] == nil {
		t.Fatalf("context: %+v", c)
	}
}

// A decision with nothing to say still has the whole shape.
func TestDecisionPayloadIsAlwaysFullShape(t *testing.T) {
	p := telemetry.Decision{Kind: "x"}.Payload()
	for _, key := range []string{"verdict", "reason", "target", "dur_ms", "attrs"} {
		if _, ok := p[key]; !ok {
			t.Fatalf("missing %s: %+v", key, p)
		}
	}
	if attrs, ok := p["attrs"].(map[string]any); !ok || len(attrs) != 0 {
		t.Fatalf("attrs: %+v", p["attrs"])
	}
}

// Every row, including one a producer wrote with no context, is a v2 row
// with the envelope guarantees.
func TestRecorderStampsV2Envelope(t *testing.T) {
	path, recorder := newRecorder(t)
	telemetry.ObserveTick(55)
	if _, err := recorder.Event("native_request", nil, false, map[string]any{"tool": "x"}); err != nil {
		t.Fatal(err)
	}
	rows, err := bridge.ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	c := rows[len(rows)-1].Context
	if c["level"] != "INFO" || c["component"] != "bridge" || c["tick"] != 55.0 || c["at"] == nil || c["trace_id"] == nil || c["span_id"] == nil {
		t.Fatalf("context: %+v", c)
	}
	if bridge.FlightSchemaVersion != 2 {
		t.Fatalf("version %d", bridge.FlightSchemaVersion)
	}
}

// Truncating an oversized decision row keeps the fixed keys that correlate it.
func TestTruncationKeepsDecisionKeys(t *testing.T) {
	path, recorder := newRecorder(t, bridge.FlightPayloadBytes(256))
	logger := telemetry.New(io.Discard, slog.LevelInfo, recorder)
	telemetry.Decision{
		Kind: "planner_step", Component: "worker", Verdict: "waiting", Reason: "no_work", Target: "Fields",
		Dur: time.Millisecond, Attrs: map[string]any{"big": strings.Repeat("x", 4096)},
	}.LogTo(context.Background(), logger)
	rows, err := bridge.ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	row := rows[len(rows)-1]
	if row.Payload["truncated"] != true || row.Payload["verdict"] != "waiting" || row.Payload["reason"] != "no_work" || row.Payload["target"] != "Fields" || row.Payload["dur_ms"] != 1.0 {
		t.Fatalf("payload: %+v", row.Payload)
	}
	if row.Context["trace_id"] == nil || row.Context["component"] != "worker" {
		t.Fatalf("context: %+v", row.Context)
	}
}
