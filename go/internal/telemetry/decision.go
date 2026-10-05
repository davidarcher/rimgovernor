package telemetry

import (
	"context"
	"fmt"
	"log/slog"
	"time"
)

// Payload keys of a decision row (flight schema v2). The shape is fixed:
// every decision row carries all four scalar keys and an attrs object, so a
// reader filters and collapses on them without knowing the kind. See
// docs/developers/contracts/flight-rows.md.
const (
	VerdictKey = "verdict"
	ReasonKey  = "reason"
	TargetKey  = "target"
	DurMsKey   = "dur_ms"
	AttrsKey   = "attrs"
)

// DecisionKey is the slog attribute that carries a Decision to the Handler,
// which writes it as a v2 row instead of the free-text msg payload.
const DecisionKey = "decision"

// Decision is one thing that happened at a planner, goal/method selection,
// admission or action-dispatch site: what was decided (Verdict), why
// (Reason), about what (Target) and how long it took (Dur), plus the
// kind-specific facts in Attrs. It is the only way to emit a v2 decision
// row; a fresh kind needs an entry in the flight-rows contract and a reader.
type Decision struct {
	// Kind is the row kind, a name from the flight-rows contract.
	Kind string
	// Component is the subsystem that decided ("clock-scheduler", "worker").
	Component string
	// Level defaults to Info; a refusal that needs attention is Warn.
	Level slog.Level
	// Verdict is the outcome word: admitted, refused, waiting, applied,
	// failed, skipped, ok. Empty is written as "".
	Verdict string
	// Reason is why, a stable short word or phrase, not a sentence with data
	// in it; the data goes in Attrs. Rows collapse on it.
	Reason string
	// Target is what the decision was about: a planner, goal, method, plan,
	// pawn, zone or tool name.
	Target string
	// Dur is how long the decision took; zero when it was not timed.
	Dur time.Duration
	// Attrs are the kind-specific fields. Values are JSON scalars, slices
	// or maps; errors, durations (as milliseconds) and Stringers are
	// rendered by Payload.
	Attrs map[string]any
}

// Payload is the row payload: the four fixed keys and attrs (an empty
// object when there are none).
func (d Decision) Payload() map[string]any {
	attrs := make(map[string]any, len(d.Attrs))
	for k, v := range d.Attrs {
		attrs[k] = attrValue(v)
	}
	return map[string]any{
		VerdictKey: d.Verdict,
		ReasonKey:  d.Reason,
		TargetKey:  d.Target,
		DurMsKey:   float64(d.Dur) / float64(time.Millisecond),
		AttrsKey:   attrs,
	}
}

// Summary is the log record's message for a decision row, "<kind> <verdict>
// <reason> <target>"; the row itself carries no free text.
func (d Decision) Summary() string {
	s := d.Kind
	for _, part := range []string{d.Verdict, d.Reason, d.Target} {
		if part != "" {
			s += " " + part
		}
	}
	return s
}

// Decide emits d through the default logger (the one serve installs the
// telemetry Handler on), stamped with the tick and the trace ctx carries.
func Decide(ctx context.Context, d Decision) { d.LogTo(ctx, slog.Default()) }

// LogTo is Decide on a specific logger.
func (d Decision) LogTo(ctx context.Context, logger *slog.Logger) {
	if ctx == nil {
		ctx = context.Background()
	}
	logger.Log(ctx, d.Level, d.Summary(), ComponentKey, d.Component, KindKey, d.Kind, DecisionKey, d)
}

// attrValue renders an attrs value for JSON: durations as milliseconds,
// errors and Stringers as text; everything else is left to encoding/json.
func attrValue(v any) any {
	switch x := v.(type) {
	case nil:
		return nil
	case error:
		return x.Error()
	case time.Duration:
		return float64(x) / float64(time.Millisecond)
	case fmt.Stringer:
		return x.String()
	}
	return v
}
