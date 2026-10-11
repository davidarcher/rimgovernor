package bridge

import (
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The report nests spans under the root by parent id, hangs rows under the
// span they were logged in, folds a completed call's native_request marker
// and native_call row into its native.call span, and keeps a marker nothing
// answered visible as "(no reply recorded)".
func TestTraceReportSpanTree(t *testing.T) {
	const trace = "aaaaaaaaaaaaaaaa0000000000000001"
	base := time.Unix(100, 0)
	at := func(ms float64) time.Time { return base.Add(time.Duration(ms * float64(time.Millisecond))) }
	span := func(id, parent, name string, from, to float64, attrs otel.Attrs) otel.Span {
		return otel.Span{TraceID: trace, SpanID: id, ParentSpanID: parent, Name: name, Start: at(from), End: at(to), Attrs: attrs}
	}
	root := span("1111111111111111", "", "scheduler.step", 0, 50, otel.Attrs{"rimgovernor.step_cause": "timer"})
	planner := span("2222222222222222", root.SpanID, "planner.food", 1, 20, nil)
	call := span("3333333333333333", planner.SpanID, "native.call", 2, 14, otel.Attrs{"rimgovernor.tool": "rimgovernor/snapshot_frame_routine", "rimgovernor.call_ms": 11.5})
	failed := span("4444444444444444", root.SpanID, "native.call", 30, 35, nil)
	failed.StatusCode, failed.StatusMsg = otel.StatusError, "refused"
	spans := []otel.Span{failed, call, root, planner} // exported in end order

	stamp := func(s otel.Span, tick float64) map[string]any {
		return map[string]any{"tick": tick, telemetry.TraceIDKey: s.TraceID, telemetry.SpanIDKey: s.SpanID}
	}
	row := func(kind string, ms float64, s otel.Span, payload map[string]any) TimelineRecord {
		return TimelineRecord{Kind: kind, WallTime: float64(at(ms).UnixNano()) / 1e9, Context: stamp(s, 50), Payload: payload}
	}
	rows := []TimelineRecord{
		row("native_request", 4, call, map[string]any{"request": 10.0, "native_tool": "rimgovernor/snapshot_frame_routine"}),
		row("native_call", 13, call, map[string]any{"request": 10.0, "native_tool": "rimgovernor/snapshot_frame_routine"}),
		row("native_frame", 15, planner, map[string]any{"outcome": "hit", "native_tool": "rimgovernor/observations_list_pawns"}),
		row("planner_step", 19, planner, map[string]any{"verdict": "admitted", "reason": "window_admitted", "target": "food", "attrs": map[string]any{"n": 1.0}}),
		row("native_request", 40, root, map[string]any{"request": 11.0, "native_tool": "rimgovernor/hung_read"}),
		{Kind: "elsewhere", WallTime: 1, Context: map[string]any{}, Payload: map[string]any{}}, // no trace id
	}
	traces := SummarizeTraces(spans, rows)
	found, ok := FindTrace(traces, trace)
	if !ok || len(traces) != 1 || len(found.Spans) != 4 || len(found.Rows) != 5 || !found.HasTick || found.Tick != 50 || found.Root != "scheduler.step step_cause=timer" {
		t.Fatalf("summary: %+v", found)
	}
	var b strings.Builder
	WriteTraceReport(&b, found)
	lines := strings.Split(strings.TrimRight(b.String(), "\n"), "\n")
	want := []string{
		"trace " + trace + ": 4 spans, 5 rows over 50.0ms, tick 50",
		"    at ms   dur ms  span      what",
		"      0.0     50.0  11111111  scheduler.step step_cause=timer",
		"      1.0     19.0  22222222    planner.food",
		"      2.0     12.0  33333333      native.call call_ms=11.5 tool=rimgovernor/snapshot_frame_routine",
		"     15.0        -  22222222      frame hit rimgovernor/observations_list_pawns",
		"     19.0        -  22222222      planner_step admitted window_admitted food n=1",
		"     30.0      5.0  44444444    native.call  ERROR: refused",
		"     40.0        -  11111111    native rimgovernor/hung_read  (no reply recorded)",
	}
	if len(lines) != len(want) {
		t.Fatalf("report:\n%s", b.String())
	}
	for i := range want {
		if lines[i] != want[i] {
			t.Fatalf("line %d:\n got %q\nwant %q\nreport:\n%s", i, lines[i], want[i], b.String())
		}
	}

	var index strings.Builder
	WriteTraceIndex(&index, traces)
	if !strings.Contains(index.String(), trace) || !strings.Contains(index.String(), "scheduler.step step_cause=timer") {
		t.Fatalf("index:\n%s", index.String())
	}
	index.Reset()
	WriteTraceIndex(&index, nil)
	if !strings.Contains(index.String(), "no traces") {
		t.Fatalf("empty index: %q", index.String())
	}
	if _, ok = FindTrace(traces, "missing"); ok {
		t.Fatal("found a trace nothing carries")
	}

	j := found.JSON()
	hung := 0
	for _, r := range j.Rows {
		if r.NoReply {
			hung++
			if r.Payload["native_tool"] != "rimgovernor/hung_read" {
				t.Fatalf("wrong row flagged: %+v", r)
			}
		}
	}
	if hung != 1 || len(j.Spans) != 4 || j.Tick == nil || *j.Tick != 50 {
		t.Fatalf("json: %+v", j)
	}
}
