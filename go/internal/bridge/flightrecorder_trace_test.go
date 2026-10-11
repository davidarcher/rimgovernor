package bridge

import (
	"context"
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
)

// A typed call made under a traced ctx sends the trace beside the request,
// and every row it records carries the trace; the companion's echo lands
// in the response row's timing.
func TestTracePropagatesThroughTypedCallsAndRows(t *testing.T) {
	noRollup(t)
	var sent []string
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
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

	spans := telemetrytest.Spans(t)
	callerCtx, caller := telemetry.StartSpan(context.Background(), "test", "caller")
	callerSC := caller.SpanContext()
	if _, _, err = client.Identity(callerCtx); err != nil {
		t.Fatal(err)
	}
	caller.End()
	if _, _, err = client.Identity(context.Background()); err != nil {
		t.Fatal(err)
	}
	// The call under a span sends that trace as a W3C traceparent (its
	// own native.call span as parent id); the call outside any span is a
	// trace root of its own.
	if len(sent) != 2 {
		t.Fatalf("trace arguments sent: %q", sent)
	}
	callTrace, callSpan, ok := telemetry.ParseWireTrace(sent[0])
	if !ok || !strings.HasPrefix(sent[0], "00-") || callTrace != callerSC.TraceID().String() || callSpan == callerSC.SpanID().String() {
		t.Fatalf("traced call sent %q, want a traceparent in trace %s under a span of its own", sent[0], callerSC.TraceID())
	}
	ownTraceID, _, ok := telemetry.ParseWireTrace(sent[1])
	if !ok || ownTraceID == callTrace {
		t.Fatalf("untraced call sent %q, want a root trace of its own", sent[1])
	}
	// The recorded structure: native.call under the caller, the synthesized
	// native.execute under native.call from the echoed timing.
	byName := map[string][]tracetest.SpanStub{}
	for _, span := range spans.GetSpans() {
		byName[span.Name] = append(byName[span.Name], span)
	}
	var nativeCall *tracetest.SpanStub
	for i, span := range byName["native.call"] {
		if span.SpanContext.TraceID() == callerSC.TraceID() {
			nativeCall = &byName["native.call"][i]
		}
	}
	if nativeCall == nil || nativeCall.Parent.SpanID() != callerSC.SpanID() || nativeCall.SpanContext.SpanID().String() != callSpan || len(byName["native.call"]) != 2 {
		t.Fatalf("native.call spans: %+v", byName["native.call"])
	}
	execute := byName["native.execute"]
	if len(execute) != 2 || execute[0].Parent.SpanID() != nativeCall.SpanContext.SpanID() && execute[1].Parent.SpanID() != nativeCall.SpanContext.SpanID() {
		t.Fatalf("native.execute spans: %+v", execute)
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	traced := 0
	for _, row := range rows {
		id, _ := row.Context[telemetry.TraceIDKey].(string)
		if id != callerSC.TraceID().String() {
			continue
		}
		traced++
		if row.Context[telemetry.SpanIDKey] != callSpan || row.Context[telemetry.ParentIDKey] != callerSC.SpanID().String() {
			t.Fatalf("row %d (%s) span/parent: %+v", row.Sequence, row.Kind, row.Context)
		}
		if row.Kind == "native_call" && row.Payload["tool"] == "games_call_tool" {
			timing, _ := row.Payload["timing"].(map[string]any)
			if timing["native_trace"] != sent[0] || timing["native_queue_ms"] != 0.5 {
				t.Fatalf("echoed trace missing from response timing: %+v", timing)
			}
		}
	}
	// The traced call's describe (games_tool_detail) and read round trips are
	// one native_call row each.
	if traced != 2 {
		t.Fatalf("traced %d rows: %+v", traced, rows)
	}
}

// A row recorded outside any span carries no trace_id (the reader leaves it
// out of the trace index); a caller-given trace is never replaced, and the
// recorder never writes to the caller's context map.
func TestRowsOutsideASpanCarryNoTrace(t *testing.T) {
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
	if _, written := given["level"]; written {
		t.Fatal("the caller's context map was written to")
	}
	if _, err = rec.Event("clock_step", map[string]any{telemetry.TraceIDKey: "t", telemetry.SpanIDKey: "s"}, false, nil); err != nil {
		t.Fatal(err)
	}
	rows, err := ReadTimeline(path)
	if err != nil || len(rows) != 3 {
		t.Fatalf("rows %d %v", len(rows), err)
	}
	if _, traced := rows[1].Context[telemetry.TraceIDKey]; traced || rows[1].Context["colony"] != "c" {
		t.Fatalf("untraced row: %+v", rows[1].Context)
	}
	if rows[2].Context[telemetry.TraceIDKey] != "t" || rows[2].Context[telemetry.SpanIDKey] != "s" {
		t.Fatalf("given trace replaced: %+v", rows[2].Context)
	}
}
