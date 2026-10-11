package telemetry

import (
	"context"
	"fmt"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/trace"
)

// Spans are the control loop's correlation: a scheduler step is a trace root;
// Round review, each planner, plan admission, the Worker's step, a Hands
// dispatch and every native bridge call are spans under it, carried on the
// context.Context. They are exported by the OTEL providers (internal/otel);
// with no provider installed they are no-ops and cost nothing.

// scopePrefix names every instrumentation scope this process creates.
const scopePrefix = "github.com/davidarcher/RimGovernor/"

// StartSpan starts a span named name under ctx's current span (a new trace
// root when ctx has none). component names the instrumentation scope. The
// global provider is read on every call so a provider installed after
// package init (serve, a test) is always the one used.
func StartSpan(ctx context.Context, component, name string, opts ...trace.SpanStartOption) (context.Context, trace.Span) {
	return otel.Tracer(scopePrefix+component).Start(ctx, name, opts...)
}

// EndSpan ends span, marking it an error when err is non-nil.
func EndSpan(span trace.Span, err error) {
	if err != nil {
		span.RecordError(err)
		span.SetStatus(codes.Error, err.Error())
	}
	span.End()
}

// Refuse marks span an error for a refusal (a dispatch the game declined, an
// admission that was not granted), which is not a Go error but is a failed
// outcome. reasons are the stable refusal words.
func Refuse(span trace.Span, reasons ...string) {
	span.SetStatus(codes.Error, "refused")
	span.SetAttributes(attribute.StringSlice("rimgovernor.refused", reasons))
}

// Remote is ctx with sc as its current span context, so a span started under
// it is a child of sc even though sc's span lives in another goroutine's
// context (the Worker nesting its steps under the scheduler's latest step).
// An invalid sc leaves ctx as it was.
func Remote(ctx context.Context, sc trace.SpanContext) context.Context {
	if !sc.IsValid() {
		return ctx
	}
	return trace.ContextWithRemoteSpanContext(ctx, sc)
}

// Traceparent is the W3C traceparent of ctx's current span, the form a bridge
// call sends the companion ("00-<32 hex trace>-<16 hex span>-<flags>"), or ""
// when ctx has no span. The companion echoes it opaquely.
func Traceparent(ctx context.Context) string {
	sc := trace.SpanContextFromContext(ctx)
	if !sc.IsValid() {
		return ""
	}
	return fmt.Sprintf("00-%s-%s-%s", sc.TraceID(), sc.SpanID(), sc.TraceFlags())
}
