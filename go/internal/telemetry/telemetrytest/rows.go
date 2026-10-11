// Package telemetrytest captures the rows a test's code logs, so a test
// asserts on rows instead of rendered text.
package telemetrytest

import (
	"log/slog"
	"strings"
	"testing"
	"time"

	"go.opentelemetry.io/otel/attribute"
	sdklog "go.opentelemetry.io/otel/sdk/log"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Row is one captured row: its kind, context (level, at, tick, component) and
// payload (a decision's verdict, reason, target, dur_ms and attrs; an event's
// msg and fields).
type Row struct {
	Kind    string
	Context map[string]any
	Payload map[string]any
}

// Rows reads the rows captured by an in-memory log exporter.
type Rows struct {
	logs *LogRecords
}

// Records is the raw log records behind the rows.
func (r *Rows) Records() []sdklog.Record { return r.logs.All() }

// All is every captured row, in order.
func (r *Rows) All() []Row {
	var out []Row
	for _, record := range r.logs.All() {
		if row, ok := rowOf(record); ok {
			out = append(out, row)
		}
	}
	return out
}

// Of is the captured rows of one kind, in order.
func (r *Rows) Of(kind string) []Row {
	var out []Row
	for _, row := range r.All() {
		if row.Kind == kind {
			out = append(out, row)
		}
	}
	return out
}

// Install makes the telemetry handler the default logger and an in-memory
// logger provider the global one until the test ends, and returns the rows
// logged meanwhile. Tests using it must not run in parallel.
func Install(t testing.TB) *Rows {
	t.Helper()
	rows := &Rows{logs: Logs(t)}
	previous := slog.Default()
	slog.SetDefault(telemetry.New())
	t.Cleanup(func() { slog.SetDefault(previous) })
	return rows
}

// rowOf rebuilds a row from its log record, the inverse of telemetry.EmitRow.
// Integers keep their int64 form.
func rowOf(record sdklog.Record) (Row, bool) {
	attrs := map[string]attribute.Value{}
	record.WalkAttributes(func(kv attribute.KeyValue) bool {
		attrs[string(kv.Key)] = kv.Value
		return true
	})
	kind := record.EventName()
	if kind == "" {
		kind = attrs[telemetry.LogKind].AsString()
	}
	if kind == "" {
		return Row{}, false
	}
	context := map[string]any{"level": record.SeverityText(), "at": record.Timestamp().UTC().Format(time.RFC3339Nano)}
	if c := attrs[telemetry.LogComponent].AsString(); c != "" {
		context[telemetry.ComponentKey] = c
	}
	if v, ok := attrs[telemetry.LogTick]; ok {
		context["tick"] = v.AsInt64()
	}
	fields := map[string]any{}
	for key, value := range attrs {
		if name, ok := strings.CutPrefix(key, telemetry.FieldPrefix); ok {
			fields[name] = plain(value)
		}
	}
	payload := map[string]any{}
	if attrs[telemetry.LogRow].AsString() == telemetry.RowDecision {
		payload[telemetry.VerdictKey] = attrs[telemetry.LogVerdict].AsString()
		payload[telemetry.ReasonKey] = attrs[telemetry.LogReason].AsString()
		payload[telemetry.TargetKey] = attrs[telemetry.LogTarget].AsString()
		payload[telemetry.DurMsKey] = attrs[telemetry.LogDurMs].AsFloat64()
		payload[telemetry.AttrsKey] = fields
	} else {
		for k, v := range fields {
			payload[k] = v
		}
		if body := record.Body(); body.Type() == attribute.STRING && body.AsString() != "" {
			payload["msg"] = body.AsString()
		}
	}
	return Row{Kind: kind, Context: context, Payload: payload}, true
}

// plain converts an attribute value to its Go form: slices as []any and
// kvlists as map[string]any.
func plain(v attribute.Value) any {
	switch v.Type() {
	case attribute.SLICE:
		items := v.AsSlice()
		out := make([]any, len(items))
		for i, item := range items {
			out[i] = plain(item)
		}
		return out
	case attribute.MAP:
		out := map[string]any{}
		for _, kv := range v.AsMap() {
			out[string(kv.Key)] = plain(kv.Value)
		}
		return out
	}
	return v.AsInterface()
}
