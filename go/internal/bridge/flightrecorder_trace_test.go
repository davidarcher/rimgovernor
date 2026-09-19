package bridge

import (
	"context"
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// A typed call made under a traced ctx sends the trace beside the request,
// and every row it records carries the trace; the companion's echo lands
// in the response row's timing.
func TestTracePropagatesThroughTypedCallsAndRows(t *testing.T) {
	var sent []string
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		var outer struct {
			Request string `json:"request"`
			Trace   string `json:"trace"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			return nil, err
		}
		sent = append(sent, outer.Trace)
		result := pbResult(pbLoaded())
		var envelope map[string]any
		if err := json.Unmarshal(result.StructuredContent.(json.RawMessage), &envelope); err != nil {
			return nil, err
		}
		timing := map[string]any{"queueMs": 0.5, "executeMs": 1.5}
		if outer.Trace != "" {
			timing["trace"] = outer.Trace
		}
		envelope["timing"] = timing
		return structured(string(encode(envelope))), nil
	}}
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	client, err := open(context.Background(), "fixture-game", time.Second, rec, nil, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })

	root := telemetry.NewTrace()
	child := root.Child()
	if _, _, err = client.Identity(telemetry.WithTrace(context.Background(), child)); err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.Identity(context.Background()); err != nil {
		t.Fatal(err)
	}
	// The untraced call is a root trace of its own, minted once for the
	// whole operation.
	if len(sent) != 2 || sent[0] != child.Wire() || sent[1] == "" || sent[1] == child.Wire() || !strings.HasSuffix(sent[1], "/"+sent[1][:16]) {
		t.Fatalf("trace arguments sent: %q, want [%q <own root>]", sent, child.Wire())
	}
	own := sent[1][:16]
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	traced, untraced := 0, 0
	for _, row := range rows {
		id, _ := row.Context[telemetry.TraceIDKey].(string)
		if id == "" {
			t.Fatalf("row %d (%s) has no trace_id: %+v", row.Sequence, row.Kind, row.Context)
		}
		if id != root.TraceID {
			untraced++
			continue
		}
		traced++
		if row.Context[telemetry.SpanIDKey] != child.SpanID || row.Context[telemetry.ParentIDKey] != root.SpanID {
			t.Fatalf("row %d (%s) span/parent: %+v", row.Sequence, row.Kind, row.Context)
		}
		if row.Kind == "native_response" && row.Payload["tool"] == "games_call_tool" {
			timing, _ := row.Payload["timing"].(map[string]any)
			if timing["native_trace"] != child.Wire() || timing["native_queue_ms"] != 0.5 {
				t.Fatalf("echoed trace missing from response timing: %+v", timing)
			}
		}
	}
	// The traced call's describe (games_tool_detail) and read round trips
	// and its decode row. The untraced call's request, reply and decode
	// rows share its own root; the coverage row is a single-row trace.
	if traced != 5 || untraced != 4 {
		t.Fatalf("traced %d untraced %d rows: %+v", traced, untraced, rows)
	}
	if ownTrace, ok := FindTrace(rows, own); !ok || len(ownTrace.Rows) != 3 || ownTrace.Root != "native rimgovernor/lifecycle_read_identity" {
		t.Fatalf("the untraced call's rows do not share its operation trace: %+v", ownTrace)
	}
	summaries := SummarizeTraces(rows)
	found, ok := FindTrace(rows, root.TraceID)
	if !ok || len(found.Rows) != 5 || found.Kinds["native_request"] != 2 || found.Root != "native rimgovernor/lifecycle_read_identity" {
		t.Fatalf("summary: %+v (of %d)", found, len(summaries))
	}
	var report strings.Builder
	WriteTraceReport(&report, found)
	text := report.String()
	if !strings.Contains(text, "trace "+root.TraceID+": 5 rows") || !strings.Contains(text, "native rimgovernor/lifecycle_read_identity") || !strings.Contains(text, "native queue 0.5 exec 1.5") {
		t.Fatalf("report:\n%s", text)
	}
	// The response and decode rows fold into the request line.
	if strings.Count(text, "\n") != 4 || !strings.Contains(text, "games_tool_detail rimgovernor/lifecycle_read_identity") {
		t.Fatalf("report lines:\n%s", text)
	}
	var index strings.Builder
	WriteTraceIndex(&index, summaries)
	if !strings.Contains(index.String(), root.TraceID) {
		t.Fatalf("index:\n%s", index.String())
	}
}

// The renderer nests spans under the root by parent_id, joins a reply to
// its request, keeps an unmatched reply visible, and renders kinded rows
// with their attributes.
func TestTraceReportWaterfall(t *testing.T) {
	root := telemetry.NewTrace()
	worker := root.Child()
	dispatch := worker.Child()
	stamp := func(tr telemetry.Trace, tick int64) map[string]any {
		m := map[string]any{"tick": float64(tick)}
		tr.Stamp(m)
		return m
	}
	rows := []TimelineRecord{
		{Kind: "native_request", Sequence: 10, WallTime: 100.000, Context: stamp(root, 50), Payload: map[string]any{"tool": "games_call_tool", "native_tool": "rimgovernor/observations_read_bundle"}},
		{Kind: "native_response", Sequence: 11, WallTime: 100.012, Context: stamp(root, 50), Payload: map[string]any{"request": float64(10), "native_tool": "rimgovernor/observations_read_bundle", "timing": map[string]any{"total_ms": 12.0, "gate_wait_ms": 0.0, "call_ms": 11.5, "decode_ms": 0.2}}},
		{Kind: "native_cache_hit", Sequence: 12, WallTime: 100.013, Context: stamp(root, 50), Payload: map[string]any{"tool": "games_call_tool", "native_tool": "rimgovernor/observations_list_pawns"}},
		{Kind: "native_request", Sequence: 13, WallTime: 100.020, Context: stamp(dispatch, 50), Payload: map[string]any{"tool": "games_call_tool", "native_tool": "rimgovernor/operations_execute"}},
		{Kind: "native_error", Sequence: 14, WallTime: 100.025, Context: stamp(dispatch, 50), Payload: map[string]any{"request": float64(13), "native_tool": "rimgovernor/operations_execute", "error": "refused", "timing": map[string]any{"total_ms": 5.0, "call_ms": 4.9}}},
		{Kind: "worker_dispatch", Sequence: 15, WallTime: 100.026, Context: stamp(dispatch, 50), Payload: map[string]any{"reads": float64(1), "receipt": "refused"}},
		{Kind: "worker_outcome", Sequence: 16, WallTime: 100.027, Context: stamp(worker, 50), Payload: map[string]any{"msg": "worker outcome", "outcome": "refused"}},
		{Kind: "native_response", Sequence: 17, WallTime: 100.030, Context: stamp(root, 50), Payload: map[string]any{"native_tool": "rimgovernor/clock_read_status"}},
		{Kind: "scheduler_step", Sequence: 18, WallTime: 100.040, Context: stamp(root, 51), Payload: map[string]any{"msg": "step done", "admitted": true}},
	}
	found, ok := FindTrace(rows, root.TraceID)
	if !ok || found.Root != "scheduler_step: step done" || !found.HasTick || found.Tick != 50 {
		t.Fatalf("summary: %+v", found)
	}
	var b strings.Builder
	WriteTraceReport(&b, found)
	lines := strings.Split(strings.TrimRight(b.String(), "\n"), "\n")
	want := []string{
		"trace " + root.TraceID + ": 9 rows over 40.0ms, tick 50, sequence 10..18",
		"    at ms   dur ms  span      row",
		"      0.0     12.0  " + shortID(root.SpanID) + "  native rimgovernor/observations_read_bundle  gate 0.0 call 11.5 decode 0.2",
		"     13.0        -  " + shortID(root.SpanID) + "  cache hit rimgovernor/observations_list_pawns",
		"     20.0      5.0  " + shortID(dispatch.SpanID) + "      native rimgovernor/operations_execute  gate 0.0 call 4.9 decode 0.0  error: refused",
		"     26.0        -  " + shortID(dispatch.SpanID) + "      worker_dispatch reads=1 receipt=refused",
		"     27.0        -  " + shortID(worker.SpanID) + "    worker_outcome \"worker outcome\" outcome=refused",
		"     30.0        -  " + shortID(root.SpanID) + "  native_response rimgovernor/clock_read_status",
		"     40.0        -  " + shortID(root.SpanID) + "  scheduler_step \"step done\" admitted=true",
	}
	if len(lines) != len(want) {
		t.Fatalf("report:\n%s", b.String())
	}
	for i := range want {
		if lines[i] != want[i] {
			t.Fatalf("line %d:\n got %q\nwant %q\nreport:\n%s", i, lines[i], want[i], b.String())
		}
	}
	if _, ok = FindTrace(rows, "missing"); ok {
		t.Fatal("found a trace no row carries")
	}
	var index strings.Builder
	WriteTraceIndex(&index, nil)
	if !strings.Contains(index.String(), "no traced rows") {
		t.Fatalf("empty index: %q", index.String())
	}
}

// A row recorded outside any traced unit of work still carries a trace_id
// of its own, so every row in a recording is addressable.
func TestEveryRecordedRowCarriesATrace(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	given := map[string]any{"colony": "c"}
	if _, err = rec.Event("clock_step", given, false, nil); err != nil {
		t.Fatal(err)
	}
	if _, traced := given[telemetry.TraceIDKey]; traced {
		t.Fatal("the caller's context map was written to")
	}
	stamped := map[string]any{}
	telemetry.Trace{TraceID: "t", SpanID: "s"}.Stamp(stamped)
	if _, err = rec.Event("clock_step", stamped, false, nil); err != nil {
		t.Fatal(err)
	}
	rows, err := ReadTimeline(path)
	if err != nil || len(rows) != 3 {
		t.Fatalf("rows %d %v", len(rows), err)
	}
	minted, _ := rows[1].Context[telemetry.TraceIDKey].(string)
	if len(minted) != 16 || rows[1].Context[telemetry.SpanIDKey] != minted || rows[1].Context["colony"] != "c" {
		t.Fatalf("minted trace: %+v", rows[1].Context)
	}
	if rows[2].Context[telemetry.TraceIDKey] != "t" || rows[2].Context[telemetry.SpanIDKey] != "s" {
		t.Fatalf("given trace replaced: %+v", rows[2].Context)
	}
}
