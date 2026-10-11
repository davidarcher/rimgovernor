package telemetry_test

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
	"go.opentelemetry.io/otel/codes"
)

func TestSpanNestingAndTraceparent(t *testing.T) {
	spans := telemetrytest.Spans(t)
	rootCtx, root := telemetry.StartSpan(context.Background(), "test", "root")
	childCtx, child := telemetry.StartSpan(rootCtx, "test", "child")
	rootSC, childSC := root.SpanContext(), child.SpanContext()
	if childSC.TraceID() != rootSC.TraceID() || childSC.SpanID() == rootSC.SpanID() {
		t.Fatalf("child %v under root %v", childSC, rootSC)
	}
	want := "00-" + childSC.TraceID().String() + "-" + childSC.SpanID().String() + "-01"
	if got := telemetry.Traceparent(childCtx); got != want {
		t.Fatalf("traceparent %q, want %q", got, want)
	}
	if telemetry.Traceparent(context.Background()) != "" {
		t.Fatal("a ctx without a span has a traceparent")
	}
	if len(telemetry.Traceparent(childCtx)) > 64 {
		t.Fatal("the companion accepts at most 64 characters of trace")
	}
	child.End()
	root.End()
	if len(spans.GetSpans()) != 2 {
		t.Fatalf("exported %d spans", len(spans.GetSpans()))
	}
}

// A span context handed over from another goroutine parents a new span.
func TestRemoteParent(t *testing.T) {
	spans := telemetrytest.Spans(t)
	_, step := telemetry.StartSpan(context.Background(), "test", "scheduler.step")
	ctx := telemetry.Remote(context.Background(), step.SpanContext())
	_, worker := telemetry.StartSpan(ctx, "test", "worker.step")
	worker.End()
	step.End()
	if worker.SpanContext().TraceID() != step.SpanContext().TraceID() {
		t.Fatal("the worker step left the scheduler step's trace")
	}
	var stub = spans.GetSpans()[0]
	if stub.Name != "worker.step" || stub.Parent.SpanID() != step.SpanContext().SpanID() {
		t.Fatalf("worker parent %v, want %v", stub.Parent.SpanID(), step.SpanContext().SpanID())
	}
}

func TestEndSpanAndRefuseStatus(t *testing.T) {
	spans := telemetrytest.Spans(t)
	_, failed := telemetry.StartSpan(context.Background(), "test", "failed")
	telemetry.EndSpan(failed, errors.New("boom"))
	_, refused := telemetry.StartSpan(context.Background(), "test", "refused")
	telemetry.Refuse(refused, "no_stock")
	refused.End()
	_, fine := telemetry.StartSpan(context.Background(), "test", "fine")
	telemetry.EndSpan(fine, nil)
	got := map[string]codes.Code{}
	for _, s := range spans.GetSpans() {
		got[s.Name] = s.Status.Code
	}
	if got["failed"] != codes.Error || got["refused"] != codes.Error || got["fine"] != codes.Unset {
		t.Fatalf("statuses %v", got)
	}
}

func TestParseWireTrace(t *testing.T) {
	for wire, want := range map[string]string{
		"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01": "0af7651916cd43dd8448eb211c80319c/b7ad6b7169203331",
		"aaaa/bbbb":                    "aaaa/bbbb",
		"":                             "",
		"nonsense":                     "",
		"00-short-b7ad6b7169203331-01": "",
	} {
		trace, span, ok := telemetry.ParseWireTrace(wire)
		got := ""
		if ok {
			got = trace + "/" + span
		}
		if got != want || ok != (want != "") {
			t.Errorf("ParseWireTrace(%q) = %q, want %q", wire, got, want)
		}
	}
}
