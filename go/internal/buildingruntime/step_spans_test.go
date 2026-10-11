package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
	oteltrace "go.opentelemetry.io/otel/trace"
)

// One scheduler step is a trace root; its Round review and each planner it
// runs are spans under it, and the plan admission a planner makes is a span
// under that planner. The Worker's step and dispatch nest under the same
// step (TestWorkerDispatchSpansNestUnderTheSchedulerStep) and every native
// call is a span under its caller (bridge: TestTracePropagatesThroughTypedCallsAndRows).
func TestSchedulerStepSpansNestRoundPlannerAdmission(t *testing.T) {
	spans := telemetrytest.Spans(t)
	s, f := schedulerFixture(t)
	schedulerSleeping(t, s, f)
	step, err := s.Step(context.Background())
	if err != nil || step.Sleeping == nil {
		t.Fatal(step, err)
	}
	stepTrace := oteltrace.TraceID{}
	for _, span := range spans.GetSpans() {
		if span.Name == "scheduler.step" {
			stepTrace = span.SpanContext.TraceID()
		}
	}
	byName := map[string][]tracetest.SpanStub{}
	for _, span := range spans.GetSpans() {
		if span.SpanContext.TraceID() == stepTrace {
			byName[span.Name] = append(byName[span.Name], span)
		}
	}
	roots := byName["scheduler.step"]
	if len(roots) != 1 || roots[0].Parent.IsValid() {
		t.Fatalf("want one root scheduler.step: %+v", roots)
	}
	root := roots[0]
	if got := s.Trace(); !got.Equal(root.SpanContext) {
		t.Fatalf("scheduler publishes %v for the Worker to nest under, want its step %v", got, root.SpanContext)
	}
	underRoot := func(name string) tracetest.SpanStub {
		t.Helper()
		if len(byName[name]) != 1 {
			t.Fatalf("want one %s span, got %+v", name, byName[name])
		}
		span := byName[name][0]
		if span.Parent.SpanID() != root.SpanContext.SpanID() || span.SpanContext.TraceID() != root.SpanContext.TraceID() {
			t.Fatalf("%s is not a child of the step: %+v", name, span)
		}
		return span
	}
	underRoot("rounds.review")
	planner := underRoot("planner.sleeping")
	if len(byName["admission"]) != 1 {
		t.Fatalf("want one admission span: %+v", byName["admission"])
	}
	if admission := byName["admission"][0]; admission.Parent.SpanID() != planner.SpanContext.SpanID() {
		t.Fatalf("admission is not under the planner that made it: %+v", admission)
	}
}

// An admission that errors, or is refused, marks its span.
func TestAdmissionSpanStatus(t *testing.T) {
	spans := telemetrytest.Spans(t)
	if _, err := admitMethod(context.Background(), nil, store.BuildingMethodRequest{}); err == nil {
		t.Fatal("an empty request was admitted")
	}
	got := spans.GetSpans()
	if len(got) != 1 || got[0].Name != "admission" || got[0].Status.Code != codes.Error {
		t.Fatalf("admission span: %+v", got)
	}
}
