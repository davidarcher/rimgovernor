// Package telemetry is the service's structured event substrate: one
// slog.Handler that turns every record naming an event kind into a
// flight-recorder row of that kind, stamped with the scheduler's last-known
// game tick, in sequence with the bridge's native_call rows. The flight
// recorder is the only log: the handler writes nothing to stderr, and a
// record with no kind is dropped (a programming error the source scan in
// kinds_test.go catches).
//
// Call sites log through slog.Default(); serve installs the handler. A
// process that never installs one (tests, smoke binaries) keeps Go's
// default text log on stderr and records nothing.
package telemetry

import (
	"context"
	"fmt"
	"log/slog"
	"sync/atomic"
	"time"
)

// KindKey is the record attribute that makes a record a flight-recorder
// row: its value is the row kind (planner_step, dispatch, ...). A record
// without it is dropped.
const KindKey = "kind"

// ComponentKey names the subsystem a record came from ("clock-scheduler",
// "worker"); it is the row context's component.
const ComponentKey = "component"

// Recorder is the flight-recorder surface the handler writes rows to.
type Recorder interface {
	Event(kind string, context map[string]any, durable bool, payload map[string]any) (uint64, error)
}

var lastTick atomic.Int64

func init() { lastTick.Store(-1) }

// ObserveTick records the game tick the service last read; every record
// logged afterwards carries it. The scheduler and the clock poll call it on
// each status or page they take, so a flight row can be lined up with the
// game's own clock.
func ObserveTick(tick int64) { lastTick.Store(tick) }

// Tick is the last observed tick and whether one was observed.
func Tick() (int64, bool) {
	t := lastTick.Load()
	return t, t >= 0
}

// Handler writes a recorder row for each kinded record. It is safe for
// concurrent use.
type Handler struct {
	recorder Recorder
	attrs    []slog.Attr
	group    string
}

// New returns a logger on the handler: a row for every Info-or-above record
// that carries KindKey, written to recorder.
func New(recorder Recorder) *slog.Logger {
	return slog.New(NewHandler(recorder))
}

// NewHandler is New's handler, for callers composing their own logger.
func NewHandler(recorder Recorder) *Handler {
	return &Handler{recorder: recorder}
}

// Enabled admits Info and above: there is no Debug level.
func (h *Handler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= slog.LevelInfo
}

func (h *Handler) WithAttrs(attrs []slog.Attr) slog.Handler {
	clone := *h
	clone.attrs = append(append([]slog.Attr(nil), h.attrs...), h.qualify(attrs)...)
	return &clone
}

func (h *Handler) WithGroup(name string) slog.Handler {
	if name == "" {
		return h
	}
	clone := *h
	if h.group != "" {
		clone.group = h.group + "." + name
	} else {
		clone.group = name
	}
	return &clone
}

func (h *Handler) qualify(attrs []slog.Attr) []slog.Attr {
	if h.group == "" {
		return attrs
	}
	out := make([]slog.Attr, 0, len(attrs))
	for _, a := range attrs {
		out = append(out, slog.Attr{Key: h.group + "." + a.Key, Value: a.Value})
	}
	return out
}

// Handle writes one recorder row for a kinded record and drops the rest. A
// record logged under a traced ctx (WithTrace) carries the trace in its row
// context beside tick, level and component; the payload is the message plus
// the remaining attributes, or a decision's fixed shape.
func (h *Handler) Handle(ctx context.Context, r slog.Record) error {
	attrs := make([]slog.Attr, 0, len(h.attrs)+r.NumAttrs())
	attrs = append(attrs, h.attrs...)
	r.Attrs(func(a slog.Attr) bool {
		attrs = append(attrs, h.qualify([]slog.Attr{a})...)
		return true
	})
	var kind, component string
	var decision *Decision
	rest := attrs[:0:0]
	for _, a := range attrs {
		if a.Key == DecisionKey {
			if d, ok := a.Value.Resolve().Any().(Decision); ok {
				decision = &d
				continue
			}
		}
		switch a.Key {
		case KindKey:
			kind = a.Value.String()
		case ComponentKey:
			component = a.Value.String()
		default:
			rest = append(rest, a)
		}
	}
	if kind == "" || h.recorder == nil {
		return nil
	}
	tick, known := Tick()
	at := r.Time
	if at.IsZero() {
		at = time.Now()
	}
	rowContext := map[string]any{"level": r.Level.String(), "at": at.UTC().Format(time.RFC3339Nano)}
	if known {
		rowContext["tick"] = tick
	}
	if component != "" {
		rowContext[ComponentKey] = component
	}
	TraceFrom(ctx).Stamp(rowContext)
	var payload map[string]any
	if decision != nil {
		payload = decision.Payload()
	} else {
		payload = map[string]any{"msg": r.Message}
		for _, a := range rest {
			payload[a.Key] = jsonValue(a.Value)
		}
	}
	_, err := h.recorder.Event(kind, rowContext, false, payload)
	return err
}

// jsonValue is the row payload's form of an attribute: scalars as
// themselves, durations in milliseconds, errors and everything else as
// their rendered text.
func jsonValue(v slog.Value) any {
	v = v.Resolve()
	switch v.Kind() {
	case slog.KindString:
		return v.String()
	case slog.KindInt64:
		return v.Int64()
	case slog.KindUint64:
		return v.Uint64()
	case slog.KindFloat64:
		return v.Float64()
	case slog.KindBool:
		return v.Bool()
	case slog.KindDuration:
		return float64(v.Duration()) / float64(time.Millisecond)
	case slog.KindTime:
		return v.Time().UTC().Format(time.RFC3339Nano)
	case slog.KindAny:
		switch x := v.Any().(type) {
		case nil:
			return nil
		case error:
			return x.Error()
		case fmt.Stringer:
			return x.String()
		case int, int32, uint32, float32, []string, map[string]int, map[string]int64:
			return x
		}
		return fmt.Sprintf("%+v", v.Any())
	default:
		return v.String()
	}
}
