package telemetry

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"reflect"
	"sort"
	"strings"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	otellog "go.opentelemetry.io/otel/log"
	"go.opentelemetry.io/otel/trace"

	rgotel "github.com/davidarcher/RimGovernor/go/internal/otel"
)

// Every row (a decision or an event) is an OTEL log record, and a span event
// when ctx carries a recording span. The attribute schema below is what the
// log readers parse; docs/developers/contracts/otel-signals.md ("Mapping") is
// its contract.

// Log record attribute keys. All are prefixed with AttrPrefix; a row's own
// payload fields are under FieldPrefix.
const (
	AttrPrefix  = "rimgovernor."
	FieldPrefix = AttrPrefix + "attr."

	LogKind      = AttrPrefix + "kind"
	LogComponent = AttrPrefix + "component"
	LogTick      = AttrPrefix + "tick"
	LogRow       = AttrPrefix + "row"
	LogVerdict   = AttrPrefix + "verdict"
	LogReason    = AttrPrefix + "reason"
	LogTarget    = AttrPrefix + "target"
	LogDurMs     = AttrPrefix + "dur_ms"

	LogTruncated     = AttrPrefix + "truncated"
	LogOriginalBytes = AttrPrefix + "original_bytes"
	LogSHA256        = AttrPrefix + "sha256"
	LogPreview       = AttrPrefix + "preview"

	// RowDecision and RowEvent are the values of LogRow.
	RowDecision = "decision"
	RowEvent    = "event"
)

const (
	// PayloadCapBytes caps one row's encoded payload: a larger payload is
	// replaced by a preview, its size and its hash.
	PayloadCapBytes = 64 << 10
	previewBytes    = 4 << 10
)

// EmitRow writes one row as an OTEL log record and, when ctx has a recording
// span, as an event on it. rowContext carries the keys level, at, tick and
// component; a payload carrying the verdict key is a
// decision (verdict, reason, target, dur_ms, attrs), anything else an event
// whose msg is the record body and whose other fields are attributes. The
// record carries ctx's trace and span ids. With no logger provider installed
// it costs one Enabled call.
func EmitRow(ctx context.Context, kind string, rowContext, payload map[string]any) {
	emitRow(ctx, kind, rowContext, payload, false)
}

// EmitDurableRow is EmitRow for a row that must survive a machine crash: its
// record carries rimgovernor.durable, so the file sink exports and fsyncs its
// batch at once (the slow-call marker, written before a call that may hang).
func EmitDurableRow(ctx context.Context, kind string, rowContext, payload map[string]any) {
	emitRow(ctx, kind, rowContext, payload, true)
}

func emitRow(ctx context.Context, kind string, rowContext, payload map[string]any, durable bool) {
	if kind == "" {
		return
	}
	if ctx == nil {
		ctx = context.Background()
	}
	level, _ := rowContext["level"].(string)
	severity, text := severityOf(level)
	logger := otel.GetLoggerProvider().Logger(scopePrefix + "telemetry")
	param := otellog.EnabledParameters{Severity: severity, EventName: kind}
	if !logger.Enabled(ctx, param) {
		return
	}
	component, _ := rowContext[ComponentKey].(string)
	at := time.Now()
	if s, ok := rowContext["at"].(string); ok {
		if parsed, err := time.Parse(time.RFC3339Nano, s); err == nil {
			at = parsed
		}
	}
	_, decision := payload[VerdictKey]
	row := RowEvent
	if decision {
		row = RowDecision
	}
	attrs := make([]attribute.KeyValue, 0, 12+len(payload))
	attrs = append(attrs,
		attribute.String(LogKind, kind),
		attribute.String(LogComponent, component),
		attribute.String(LogRow, row),
	)
	tick, known := intOf(rowContext["tick"])
	if !known {
		tick, known = Tick()
	}
	if known {
		attrs = append(attrs, attribute.Int64(LogTick, tick))
	}
	var body string
	var fields map[string]any
	if decision {
		verdict, _ := payload[VerdictKey].(string)
		reason, _ := payload[ReasonKey].(string)
		target, _ := payload[TargetKey].(string)
		dur, _ := payload[DurMsKey].(float64)
		attrs = append(attrs,
			attribute.String(LogVerdict, verdict),
			attribute.String(LogReason, reason),
			attribute.String(LogTarget, target),
			attribute.Float64(LogDurMs, dur),
		)
		body = (Decision{Kind: kind, Verdict: verdict, Reason: reason, Target: target}).Summary()
		fields, _ = payload[AttrsKey].(map[string]any)
	} else {
		body, _ = payload["msg"].(string)
		fields = make(map[string]any, len(payload))
		for k, v := range payload {
			if k != "msg" {
				fields[k] = v
			}
		}
	}
	if encoded, err := json.Marshal(fields); err == nil && len(encoded) > PayloadCapBytes {
		sum := sha256.Sum256(encoded)
		attrs = append(attrs,
			attribute.Bool(LogTruncated, true),
			attribute.Int(LogOriginalBytes, len(encoded)),
			attribute.String(LogSHA256, hex.EncodeToString(sum[:])),
			attribute.String(LogPreview, string(encoded[:previewBytes])),
		)
		// The correlation keys survive the truncation.
		kept := map[string]any{}
		for _, key := range []string{"request", "tool", "native_tool", "category", "timing"} {
			if value, ok := fields[key]; ok && correlatable(value) {
				kept[key] = value
			}
		}
		fields = kept
	}
	for _, key := range sortedKeys(fields) {
		attrs = append(attrs, attribute.KeyValue{Key: attribute.Key(FieldPrefix + key), Value: logValue(fields[key])})
	}

	var record otellog.Record
	record.SetTimestamp(at)
	record.SetObservedTimestamp(time.Now())
	record.SetSeverity(severity)
	record.SetSeverityText(text)
	record.SetEventName(kind)
	if body != "" {
		record.SetBody(attribute.StringValue(body))
	}
	record.AddAttributes(attrs...)
	if durable {
		record.AddAttributes(rgotel.DurableAttr)
	}
	logger.Emit(ctx, record)

	if span := trace.SpanFromContext(ctx); span.IsRecording() {
		span.AddEvent(kind, trace.WithTimestamp(at), trace.WithAttributes(attrs...))
	}
}

// RemoteHex is ctx with the span named by hex trace and span ids as its remote
// parent (an id pair from another process, e.g. the mod's echoed traceparent).
// Malformed ids leave ctx as it was.
func RemoteHex(ctx context.Context, traceID, spanID string) context.Context {
	tid, err := trace.TraceIDFromHex(traceID)
	if err != nil {
		return ctx
	}
	sid, err := trace.SpanIDFromHex(spanID)
	if err != nil {
		return ctx
	}
	return Remote(ctx, trace.NewSpanContext(trace.SpanContextConfig{TraceID: tid, SpanID: sid, TraceFlags: trace.FlagsSampled, Remote: true}))
}

func severityOf(level string) (otellog.Severity, string) {
	switch strings.ToUpper(level) {
	case "ERROR":
		return otellog.SeverityError, "ERROR"
	case "WARN":
		return otellog.SeverityWarn, "WARN"
	}
	return otellog.SeverityInfo, "INFO"
}

func intOf(v any) (int64, bool) {
	switch x := v.(type) {
	case int:
		return int64(x), true
	case int64:
		return x, true
	case float64:
		return int64(x), true
	}
	return 0, false
}

func sortedKeys(m map[string]any) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// correlatable admits the small scalar fields (and one flat map of them) that
// survive truncation.
func correlatable(value any) bool {
	switch v := value.(type) {
	case string:
		return len(v) <= 256
	case int, int32, int64, uint, uint32, uint64, float64:
		return true
	case map[string]any:
		if len(v) > 16 {
			return false
		}
		for _, inner := range v {
			if _, nested := inner.(map[string]any); nested || !correlatable(inner) {
				return false
			}
		}
		return true
	}
	return false
}

// logValue converts a payload value to an OTEL log value: scalars as
// themselves, slices and maps as OTLP arrays and kvlists, errors and Stringers
// as text, anything else through its JSON form.
func logValue(v any) attribute.Value {
	switch x := v.(type) {
	case nil:
		return attribute.Value{}
	case string:
		return attribute.StringValue(x)
	case bool:
		return attribute.BoolValue(x)
	case int:
		return attribute.IntValue(x)
	case int8, int16, int32, int64:
		return attribute.Int64Value(reflect.ValueOf(x).Int())
	case uint, uint8, uint16, uint32:
		return attribute.Int64Value(int64(reflect.ValueOf(x).Uint()))
	case uint64:
		if x > 1<<63-1 {
			return attribute.StringValue(fmt.Sprint(x))
		}
		return attribute.Int64Value(int64(x))
	case float32:
		return attribute.Float64Value(float64(x))
	case float64:
		return attribute.Float64Value(x)
	case time.Duration:
		return attribute.Float64Value(float64(x) / float64(time.Millisecond))
	case json.Number:
		if i, err := x.Int64(); err == nil {
			return attribute.Int64Value(i)
		}
		f, _ := x.Float64()
		return attribute.Float64Value(f)
	case json.RawMessage:
		// A row field kept as received JSON (a native call's arguments and
		// reply) becomes the structure it encodes.
		var generic any
		d := json.NewDecoder(bytes.NewReader(x))
		d.UseNumber()
		if d.Decode(&generic) != nil {
			return attribute.StringValue(string(x))
		}
		return logValue(generic)
	case error:
		return attribute.StringValue(x.Error())
	case fmt.Stringer:
		return attribute.StringValue(x.String())
	}
	rv := reflect.ValueOf(v)
	switch rv.Kind() {
	case reflect.Slice, reflect.Array:
		items := make([]attribute.Value, rv.Len())
		for i := range items {
			items[i] = logValue(rv.Index(i).Interface())
		}
		return attribute.SliceValue(items...)
	case reflect.Map:
		if rv.Type().Key().Kind() == reflect.String {
			kvs := make([]attribute.KeyValue, 0, rv.Len())
			for _, key := range rv.MapKeys() {
				kvs = append(kvs, attribute.KeyValue{Key: attribute.Key(key.String()), Value: logValue(rv.MapIndex(key).Interface())})
			}
			sort.Slice(kvs, func(i, j int) bool { return kvs[i].Key < kvs[j].Key })
			return attribute.MapValue(kvs...)
		}
	case reflect.Pointer, reflect.Interface:
		if rv.IsNil() {
			return attribute.Value{}
		}
		return logValue(rv.Elem().Interface())
	}
	encoded, err := json.Marshal(v)
	if err != nil {
		return attribute.StringValue(fmt.Sprintf("%+v", v))
	}
	var generic any
	d := json.NewDecoder(strings.NewReader(string(encoded)))
	d.UseNumber()
	if d.Decode(&generic) != nil {
		return attribute.StringValue(string(encoded))
	}
	if _, isString := generic.(string); isString {
		return logValue(generic)
	}
	if reflect.TypeOf(generic) == reflect.TypeOf(v) {
		return attribute.StringValue(fmt.Sprintf("%+v", v))
	}
	return logValue(generic)
}
