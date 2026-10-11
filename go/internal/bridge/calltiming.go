package bridge

import (
	"context"
	"encoding/json"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/trace"
)

// annotateCallSpan puts a completed call's phases on the native.call span
// ctx carries, and, when the companion reported its main-thread timing,
// adds the native.execute child span synthesized from it. The companion
// echoes the traceparent but exports no spans of its own (it ships no
// Activity support, see docs/developers/contracts/otel-core.md), so the
// child is placed from the echoed durations: it ends when the call returned
// and starts queue+execute before that, clamped to the call's start.
func annotateCallSpan(ctx context.Context, tool string, began time.Time, call, decode time.Duration, native nativeTimingReport, ok bool) {
	span := trace.SpanFromContext(ctx)
	if !span.IsRecording() {
		return
	}
	span.SetAttributes(attribute.String("rimgovernor.tool", tool), attribute.Float64("rimgovernor.call_ms", millis(call)), attribute.Float64("rimgovernor.decode_ms", millis(decode)))
	if !ok {
		return
	}
	span.SetAttributes(attribute.Float64("rimgovernor.native_queue_ms", native.queueMs), attribute.Float64("rimgovernor.native_execute_ms", native.executeMs))
	end := began.Add(call)
	start := end.Add(-time.Duration((native.queueMs + native.executeMs) * float64(time.Millisecond)))
	if start.Before(began) {
		start = began
	}
	_, child := telemetry.StartSpan(ctx, "bridge", "native.execute", trace.WithSpanKind(trace.SpanKindServer), trace.WithTimestamp(start),
		trace.WithAttributes(attribute.String("rimgovernor.tool", tool), attribute.Bool("rimgovernor.synthesized", true),
			attribute.Float64("rimgovernor.native_queue_ms", native.queueMs), attribute.Float64("rimgovernor.native_execute_ms", native.executeMs)))
	child.End(trace.WithTimestamp(end))
}

// callTiming splits one Client.operation into the phases a flight-recorder
// consumer can attribute: waiting for the concurrency gate, the MCP round
// trip over GABP, decoding the receipt, and, for typed adapters,
// ProtoJSON reply decoding. The round trip's native share (main-thread
// queueing and the tool body) is reported by the companion inside the reply
// wrapper and split out by nativeTiming. It is
// created by operation, attached to ctx, filled in by core and protoCall,
// and only ever read by the goroutine running that one operation.
type callTiming struct {
	began    time.Time
	gateWait time.Duration
	// admission is the class the call was admitted under and what it saw
	// of the queue.
	admission admissionOutcome
}

type callTimingKey struct{}

func withCallTiming(ctx context.Context, timing *callTiming) context.Context {
	return context.WithValue(ctx, callTimingKey{}, timing)
}

func callTimingFrom(ctx context.Context) *callTiming {
	timing, _ := ctx.Value(callTimingKey{}).(*callTiming)
	return timing
}

func millis(d time.Duration) float64 { return float64(d) / float64(time.Millisecond) }

// nativeTimingReport is the companion's own phase report from a
// ProtoBoundary reply wrapper: {"payload": "...", "timing": {"queueMs":
// <wait for the main thread>, "executeMs": <tool body>, "trace": <the
// trace argument the call sent, echoed>, "queueDepth": <hops waiting for
// the main thread when this one was queued>}}. trace is empty and
// queueDepth -1 when the call sent none or the companion predates them.
//
// observation and frames are the observation capture account and the frame
// recorder's session counters, copied through verbatim so the phase
// report reads the companion's own field names; both are nil when the
// companion reported none.
type nativeTimingReport struct {
	queueMs, executeMs float64
	trace              string
	queueDepth         int
	observation        map[string]any
	frames             map[string]any
}

// nativeTiming reads the companion's phase report out of a reply wrapper.
// Companions that predate the field report nothing, so a consumer can tell
// absent from zero.
func nativeTiming(structured json.RawMessage) (nativeTimingReport, bool) {
	if len(structured) == 0 {
		return nativeTimingReport{}, false
	}
	var wrapper struct {
		Timing *struct {
			QueueMs    *float64 `json:"queueMs"`
			ExecuteMs  *float64 `json:"executeMs"`
			Trace      string   `json:"trace"`
			QueueDepth *int     `json:"queueDepth"`
			// Both are absent when the companion supplies no observation account.
			Observation map[string]any `json:"observation"`
			Frames      map[string]any `json:"frames"`
		} `json:"timing"`
	}
	if json.Unmarshal(structured, &wrapper) != nil || wrapper.Timing == nil || wrapper.Timing.QueueMs == nil || wrapper.Timing.ExecuteMs == nil {
		return nativeTimingReport{}, false
	}
	if *wrapper.Timing.QueueMs < 0 || *wrapper.Timing.ExecuteMs < 0 {
		return nativeTimingReport{}, false
	}
	report := nativeTimingReport{queueMs: *wrapper.Timing.QueueMs, executeMs: *wrapper.Timing.ExecuteMs, trace: wrapper.Timing.Trace, queueDepth: -1}
	if wrapper.Timing.QueueDepth != nil && *wrapper.Timing.QueueDepth >= 0 {
		report.queueDepth = *wrapper.Timing.QueueDepth
	}
	report.observation, report.frames = wrapper.Timing.Observation, wrapper.Timing.Frames
	return report, true
}

// nativeToolOf names the inner native tool for the wrappers that carry
// one, so phase totals aggregate per rimgovernor/* method rather than under
// games_call_tool. Other core tools report their own name.
func nativeToolOf(name string, arguments json.RawMessage) string {
	if name != "games_call_tool" && name != "games_tool_detail" {
		return name
	}
	var inner struct {
		Tool string `json:"tool"`
	}
	if json.Unmarshal(arguments, &inner) != nil || inner.Tool == "" {
		return name
	}
	return inner.Tool
}
