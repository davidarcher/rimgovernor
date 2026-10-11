package buildingruntime

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	oteltrace "go.opentelemetry.io/otel/trace"
)

// endDispatchSpan ends a hands.dispatch span with the batch's outcome: a
// returned error marks the span an error, a refusal the game or the executor
// declined (no error, the write was not applied) marks it refused with the
// reasons, and anything else leaves it ok.
func endDispatchSpan(span oteltrace.Span, results []executor.Result, errs []error) {
	if err := errors.Join(errs...); err != nil {
		telemetry.EndSpan(span, err)
		return
	}
	var reasons []string
	for _, result := range results {
		reasons = append(reasons, workerRefusedReasons(result)...)
	}
	if len(reasons) > 0 {
		telemetry.Refuse(span, reasons...)
	}
	span.End()
}
