package telemetry

import (
	"context"
	"fmt"
	"math/rand/v2"
)

// Trace correlates the rows one unit of service work leaves: a scheduler
// step is a trace root, the Worker's step and each dispatch it makes are
// spans under it, and every flight row written under the context that
// carries the trace (native calls, kinded records, read tallies) is
// stamped with it. Ids are 16 hex digits; ParentID is empty at the root.
type Trace struct {
	TraceID  string
	SpanID   string
	ParentID string
}

// The row context keys a Trace is written under.
const (
	TraceIDKey  = "trace_id"
	SpanIDKey   = "span_id"
	ParentIDKey = "parent_id"
)

// NewTrace starts a trace: a fresh trace id whose root span is the trace.
func NewTrace() Trace {
	id := newID()
	return Trace{TraceID: id, SpanID: id}
}

// Child is a new span under t in the same trace. The child of an empty
// Trace is a new trace root, so a caller can nest under whatever it was
// handed without checking.
func (t Trace) Child() Trace {
	if t.TraceID == "" {
		return NewTrace()
	}
	return Trace{TraceID: t.TraceID, SpanID: newID(), ParentID: t.SpanID}
}

// Empty reports whether t carries no trace.
func (t Trace) Empty() bool { return t.TraceID == "" }

// Stamp writes t into a row context; an empty Trace writes nothing.
func (t Trace) Stamp(rowContext map[string]any) {
	if t.TraceID == "" {
		return
	}
	rowContext[TraceIDKey] = t.TraceID
	rowContext[SpanIDKey] = t.SpanID
	if t.ParentID != "" {
		rowContext[ParentIDKey] = t.ParentID
	}
}

// Wire is the form a bridge call sends the companion and the companion
// echoes in its timing object: "<trace_id>/<span_id>".
func (t Trace) Wire() string {
	if t.TraceID == "" {
		return ""
	}
	return t.TraceID + "/" + t.SpanID
}

type traceKey struct{}

// WithTrace attaches t to ctx. An empty Trace leaves ctx as it was.
func WithTrace(ctx context.Context, t Trace) context.Context {
	if t.TraceID == "" {
		return ctx
	}
	return context.WithValue(ctx, traceKey{}, t)
}

// TraceFrom is the Trace WithTrace attached, or an empty Trace.
func TraceFrom(ctx context.Context) Trace {
	if ctx == nil {
		return Trace{}
	}
	t, _ := ctx.Value(traceKey{}).(Trace)
	return t
}

// EnsureTrace returns ctx with a trace on it: the one it carries, or a new
// root, so a unit of work that may run under a caller's trace or on its
// own (a scheduler step driven by a test, a poll) is stamped either way.
func EnsureTrace(ctx context.Context) (context.Context, Trace) {
	if t := TraceFrom(ctx); !t.Empty() {
		return ctx, t
	}
	t := NewTrace()
	return WithTrace(ctx, t), t
}

// newID is a random 64-bit id in hex. Ids only need to be distinct within
// one recording, so the fast generator is enough.
func newID() string { return fmt.Sprintf("%016x", rand.Uint64()) }
