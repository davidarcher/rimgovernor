package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// A combat's stops are logged by event kind, the tick-budget backstop
// among them, with resume latency and ticks between stops; the summary
// is written when a colony window follows and a stop outside combat is
// not counted.
func TestCombatStopMetrics(t *testing.T) {
	rows := telemetrytest.Install(t)
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
	stops, summaries := rows.Of("clock_stop"), rows.Of("combat_summary")
	if len(stops) != 3 || stops[0].Payload["event"] != "entered_range" || stops[0].Payload["ticks_since_stop"] != int64(40) ||
		stops[1].Payload["event"] != "tick_budget" || stops[1].Payload["ticks_since_stop"] != int64(300) ||
		stops[1].Payload["resume_latency_ms"] != int64(200) ||
		// The stop ending the combat resumes into the colony window: its
		// latency is the review's, not a combat reaction.
		stops[2].Payload["event"] != "downed" || stops[2].Payload["resume_latency_ms"] != nil {
		t.Fatal(stops)
	}
	if len(summaries) != 1 {
		t.Fatal(summaries)
	}
	summary := summaries[0].Payload
	byEvent, _ := summary["by_event"].(map[string]int)
	if summary["stops"] != int64(3) || byEvent["entered_range"] != 1 || byEvent["tick_budget"] != 1 || byEvent["downed"] != 1 ||
		summary["resume_latency_p50_ms"] != int64(100) || summary["resume_latency_p95_ms"] != int64(200) ||
		summary["ticks_between_stops_p50"] != int64(60) || summary["ticks_between_stops_p95"] != int64(300) {
		t.Fatal(summary)
	}
}
