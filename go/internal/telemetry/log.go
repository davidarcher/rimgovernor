// Package telemetry is the service's structured event substrate: one
// slog.Handler that renders every record to stderr stamped with wall time
// and the scheduler's last-known game tick, and mirrors every record that
// names an event kind into the flight recorder as a row of that kind, in
// sequence with the bridge's native_* rows.
//
// Call sites log through slog.Default(); serve installs the handler. A
// process that never installs one (tests, smoke binaries) keeps Go's
// default text log on stderr and records nothing.
package telemetry

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// KindKey is the record attribute that makes a record a flight-recorder
// row: its value is the row kind (scheduler_step, worker_outcome, ...). A
// record without it is rendered to stderr only.
const KindKey = "kind"

// ComponentKey names the subsystem a record came from ("clock-scheduler",
// "worker"); stderr renders it in brackets after the level, so a grep for
// "[clock-worker] step failed" keeps working across the stamp.
const ComponentKey = "component"

// Recorder is the flight-recorder surface the handler writes rows to.
type Recorder interface {
	Event(kind string, context map[string]any, durable bool, payload map[string]any) (uint64, error)
}

var lastTick atomic.Int64

func init() { lastTick.Store(-1) }

// ObserveTick records the game tick the service last read; every record
// logged afterwards carries it. The scheduler and the clock poll call it on
// each status or page they take, so a stderr line and a flight row can be
// lined up with the game's own clock.
func ObserveTick(tick int64) { lastTick.Store(tick) }

// Tick is the last observed tick and whether one was observed.
func Tick() (int64, bool) {
	t := lastTick.Load()
	return t, t >= 0
}

// Handler renders records to a writer and mirrors kinded records to a
// recorder. It is safe for concurrent use.
type Handler struct {
	mu       *sync.Mutex
	out      io.Writer
	level    slog.Leveler
	recorder Recorder
	attrs    []slog.Attr
	group    string
}

// New returns a logger on the handler. out receives every record at or
// above level (a nil level means Info); recorder, when not nil, receives a
// row for every such record that carries KindKey.
func New(out io.Writer, level slog.Leveler, recorder Recorder) *slog.Logger {
	return slog.New(NewHandler(out, level, recorder))
}

// NewHandler is New's handler, for callers composing their own logger.
func NewHandler(out io.Writer, level slog.Leveler, recorder Recorder) *Handler {
	if level == nil {
		level = slog.LevelInfo
	}
	return &Handler{mu: new(sync.Mutex), out: out, level: level, recorder: recorder}
}

func (h *Handler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= h.level.Level()
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

// Handle writes one stderr line and, for a kinded record, one recorder
// row. The line is
//
//	<RFC3339 ms UTC> tick=<n|-> <LEVEL> [<component>] <message> k=v ...
//
// The message is written verbatim (a multi-line message keeps its lines)
// so the existing stderr parsers see the text they always saw after the
// stamp. A kinded record logged under a traced ctx (WithTrace) renders
// trace=<id> last on the line and carries the trace in its row context
// beside tick, level and component; the payload is the message plus the
// remaining attributes.
func (h *Handler) Handle(ctx context.Context, r slog.Record) error {
	attrs := make([]slog.Attr, 0, len(h.attrs)+r.NumAttrs())
	attrs = append(attrs, h.attrs...)
	r.Attrs(func(a slog.Attr) bool {
		attrs = append(attrs, h.qualify([]slog.Attr{a})...)
		return true
	})
	var kind, component string
	rest := attrs[:0:0]
	for _, a := range attrs {
		switch a.Key {
		case KindKey:
			kind = a.Value.String()
		case ComponentKey:
			component = a.Value.String()
		default:
			rest = append(rest, a)
		}
	}
	tick, known := Tick()
	trace := TraceFrom(ctx)
	at := r.Time
	if at.IsZero() {
		at = time.Now()
	}
	var b strings.Builder
	b.WriteString(at.UTC().Format("2006-01-02T15:04:05.000Z"))
	b.WriteString(" tick=")
	if known {
		b.WriteString(strconv.FormatInt(tick, 10))
	} else {
		b.WriteByte('-')
	}
	b.WriteByte(' ')
	b.WriteString(r.Level.String())
	if component != "" {
		b.WriteString(" [")
		b.WriteString(component)
		b.WriteByte(']')
	}
	b.WriteByte(' ')
	b.WriteString(r.Message)
	for _, a := range rest {
		b.WriteByte(' ')
		b.WriteString(a.Key)
		b.WriteByte('=')
		b.WriteString(quote(render(a.Value)))
	}
	if kind != "" && !trace.Empty() {
		b.WriteString(" trace=")
		b.WriteString(trace.TraceID)
	}
	b.WriteByte('\n')
	h.mu.Lock()
	_, err := io.WriteString(h.out, b.String())
	h.mu.Unlock()
	if kind != "" && h.recorder != nil {
		rowContext := map[string]any{"level": r.Level.String(), "at": at.UTC().Format(time.RFC3339Nano)}
		if known {
			rowContext["tick"] = tick
		}
		if component != "" {
			rowContext[ComponentKey] = component
		}
		trace.Stamp(rowContext)
		payload := map[string]any{"msg": r.Message}
		for _, a := range rest {
			payload[a.Key] = jsonValue(a.Value)
		}
		if _, rowErr := h.recorder.Event(kind, rowContext, false, payload); rowErr != nil && err == nil {
			err = rowErr
		}
	}
	return err
}

func render(v slog.Value) string {
	v = v.Resolve()
	switch v.Kind() {
	case slog.KindString:
		return v.String()
	case slog.KindAny:
		if v.Any() == nil {
			return "<nil>"
		}
		if err, ok := v.Any().(error); ok {
			return err.Error()
		}
		return fmt.Sprintf("%+v", v.Any())
	default:
		return v.String()
	}
}

// quote protects a value that would otherwise split or hide a key=value
// token; plain words and numbers stay bare so the lines read like the
// printf output they replace.
func quote(s string) string {
	if s == "" || strings.ContainsAny(s, " \t\n\"=") {
		return strconv.Quote(s)
	}
	return s
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
		case int, int32, uint32, float32, []string, map[string]int64:
			return x
		}
		return fmt.Sprintf("%+v", v.Any())
	default:
		return v.String()
	}
}
