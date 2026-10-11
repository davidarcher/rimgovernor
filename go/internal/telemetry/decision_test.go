package telemetry_test

import (
	"context"
	"errors"
	"log/slog"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// A decision row has the fixed payload shape and the full context.
func TestDecisionRowRoundTrip(t *testing.T) {
	rows := telemetrytest.Install(t)
	telemetry.ObserveTick(1234)
	telemetry.Decision{
		Kind: "admission", Component: "clock-scheduler", Level: slog.LevelWarn,
		Verdict: "refused", Reason: "critical_wave_budget", Target: "window",
		Dur:   1500 * time.Microsecond,
		Attrs: map[string]any{"err": errors.New("held"), "wait": 2 * time.Second, "n": 3},
	}.LogTo(context.Background(), slog.Default())
	got := rows.Of("admission")
	if len(got) != 1 {
		t.Fatalf("rows: %+v", rows.All())
	}
	row := got[0]
	for key, want := range map[string]any{"verdict": "refused", "reason": "critical_wave_budget", "target": "window", "dur_ms": 1.5} {
		if row.Payload[key] != want {
			t.Fatalf("payload %s = %v, want %v: %+v", key, row.Payload[key], want, row.Payload)
		}
	}
	if _, free := row.Payload["msg"]; free {
		t.Fatalf("a decision row carries no free text: %+v", row.Payload)
	}
	attrs, _ := row.Payload["attrs"].(map[string]any)
	if attrs["err"] != "held" || attrs["wait"] != 2000.0 || attrs["n"] != int64(3) {
		t.Fatalf("attrs: %+v", attrs)
	}
	c := row.Context
	if c["level"] != "WARN" || c["component"] != "clock-scheduler" || c["tick"] != int64(1234) || c["at"] == nil {
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
