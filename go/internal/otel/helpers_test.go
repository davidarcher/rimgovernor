package otel

import (
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/trace"
)

func traceAttr(i int) trace.SpanStartEventOption {
	return trace.WithAttributes(attribute.Int("i", i), attribute.String("verdict", "ok"))
}
