package buildingruntime

import (
	"bytes"
	"context"
	"log/slog"
	"strings"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// A combat's stops are logged by event kind, the tick-budget backstop
// among them, with resume latency and ticks between stops; the summary
// is written when a colony window follows and a stop outside combat is
// not counted (#849).
func TestCombatStopMetrics(t *testing.T) {
	var buf bytes.Buffer
	prior := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&buf, nil)))
	t.Cleanup(func() { slog.SetDefault(prior) })
	ctx := context.Background()
	at := time.UnixMilli(1_000_000)
	stop := func(reason k.StopReason, event k.CombatEvent, ago time.Duration) *k.Stopped {
		out := &k.Stopped{Reason: reason.Enum(), StoppedAtUnixMs: proto.Int64(at.Add(-ago).UnixMilli())}
		if event != k.CombatEvent_COMBAT_EVENT_UNSPECIFIED {
			out.CombatEvent = event.Enum()
		}
		return out
	}
	var m combatStopMetrics
	// A colony stop before the fight is not a combat stop.
	m.admitted(ctx, stop(k.StopReason_STOP_REASON_HOSTILE, 0, time.Second), 1000, at, true)
	m.admitted(ctx, stop(k.StopReason_STOP_REASON_COMBAT_EVENT, k.CombatEvent_COMBAT_EVENT_ENTERED_RANGE, 100*time.Millisecond), 1040, at, true)
	m.admitted(ctx, stop(k.StopReason_STOP_REASON_TICK_BUDGET, 0, 200*time.Millisecond), 1340, at, true)
	m.admitted(ctx, stop(k.StopReason_STOP_REASON_COMBAT_EVENT, k.CombatEvent_COMBAT_EVENT_DOWNED, 400*time.Millisecond), 1400, at, false)
	if m.active || m.byKind != nil {
		t.Fatal("combat metrics not reset after the colony window", m)
	}
	lines := strings.Split(strings.TrimSpace(buf.String()), "\n")
	var stops []string
	var summary string
	for _, line := range lines {
		switch {
		case strings.Contains(line, "kind=clock_stop "):
			stops = append(stops, line)
		case strings.Contains(line, "kind=combat_summary "):
			summary = line
		}
	}
	if len(stops) != 3 || !strings.Contains(stops[0], "event=entered_range") || !strings.Contains(stops[0], "ticks_since_stop=40") ||
		!strings.Contains(stops[1], "event=tick_budget") || !strings.Contains(stops[1], "ticks_since_stop=300") ||
		!strings.Contains(stops[1], "resume_latency_ms=200") ||
		// The stop ending the combat resumes into the colony window: its
		// latency is the review's, not a combat reaction (#890).
		!strings.Contains(stops[2], "event=downed") || strings.Contains(stops[2], "resume_latency_ms") {
		t.Fatal(stops)
	}
	for _, want := range []string{"stops=3", "entered_range:1", "tick_budget:1", "downed:1", "resume_latency_p50_ms=100", "resume_latency_p95_ms=200", "ticks_between_stops_p50=60", "ticks_between_stops_p95=300"} {
		if !strings.Contains(summary, want) {
			t.Fatal(want, summary)
		}
	}
}
