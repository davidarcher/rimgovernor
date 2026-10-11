package telemetry

import (
	"context"
	"strings"

	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/trace"
)

// TEMPORARY adapter for `rimgovernor trace` (#2784): flight rows still carry
// the ids of the span they were logged under so the recorder reader can group
// them. Removed with flight.jsonl by the Retire child (#2793), when the
// reader walks traces.jsonl instead.

// The row context keys the adapter writes.
const (
	TraceIDKey  = "trace_id"
	SpanIDKey   = "span_id"
	ParentIDKey = "parent_id"
)

// StampTrace writes ctx's current span ids into a flight row context; a ctx
// with no span writes nothing.
func StampTrace(ctx context.Context, rowContext map[string]any) {
	if ctx == nil {
		return
	}
	span := trace.SpanFromContext(ctx)
	sc := span.SpanContext()
	if !sc.IsValid() {
		return
	}
	rowContext[TraceIDKey] = sc.TraceID().String()
	rowContext[SpanIDKey] = sc.SpanID().String()
	if ro, ok := span.(sdktrace.ReadOnlySpan); ok {
		if parent := ro.Parent(); parent.IsValid() {
			rowContext[ParentIDKey] = parent.SpanID().String()
		}
	}
}

// ParseWireTrace splits what a companion log line or timing block carries:
// a W3C traceparent ("00-<trace>-<span>-<flags>") or the older
// "<trace>/<span>". It reports false for anything else.
func ParseWireTrace(wire string) (traceID, spanID string, ok bool) {
	if parts := strings.Split(wire, "-"); len(parts) == 4 && len(parts[1]) == 32 && len(parts[2]) == 16 {
		return parts[1], parts[2], true
	}
	if t, s, cut := strings.Cut(wire, "/"); cut && t != "" && s != "" {
		return t, s, true
	}
	return "", "", false
}
