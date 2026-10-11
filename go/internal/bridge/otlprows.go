package bridge

import (
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Rows over OTLP. Every decision and event row, and the bridge's native_call,
// native_request, native_frame and native_poll rows, is an OTEL log record in
// logs.jsonl (docs/developers/contracts/flight-rows.md, "OTEL mapping").
// RecordFromLog is the inverse of telemetry.EmitRow: it rebuilds the row's
// TimelineRecord, so the readers of rows (SummarizePhases, the kind helpers in
// flightrows.go, the acceptance oracles) work on OTLP output unchanged.

// ReadOTLPTimeline reads the logs ring at path (a logs.jsonl, with its rotated
// segments) as rows, oldest first. Sequence numbers are the records' order in
// the ring (1-based); Run is the emitting process's service.instance.id. A
// missing ring is an empty timeline.
func ReadOTLPTimeline(path string) ([]TimelineRecord, error) {
	var rows []TimelineRecord
	err := otel.ReadLogs(path, func(l otel.Log) error {
		if rec, ok := RecordFromLog(l); ok {
			rec.Sequence, rec.HasSeq = uint64(len(rows)+1), true
			rows = append(rows, rec)
		}
		return nil
	})
	return rows, err
}

// RecordFromLog rebuilds a row from its log record. A record that is no row
// (no event name) reports false.
//
// Context carries level, at, component, tick (when the record had one) and the
// record's trace_id and span_id. A decision's payload is verdict, reason,
// target, dur_ms and attrs; an event's is msg (the record body) and its
// fields. All numbers are float64, as a JSON-decoded flight row held them. A
// record whose payload was capped carries truncated, original_bytes, sha256
// and preview in its payload beside the correlation fields that survived.
func RecordFromLog(l otel.Log) (TimelineRecord, bool) {
	kind := l.EventName
	if kind == "" {
		kind = l.Attrs.Str(telemetry.LogKind)
	}
	if kind == "" {
		return TimelineRecord{}, false
	}
	level := strings.ToUpper(l.SeverityText)
	if level == "" {
		switch {
		case l.Severity >= 17:
			level = "ERROR"
		case l.Severity >= 13:
			level = "WARN"
		default:
			level = "INFO"
		}
	}
	context := map[string]any{"level": level, "at": l.Time.UTC().Format(time.RFC3339Nano)}
	if c := l.Attrs.Str(telemetry.LogComponent); c != "" {
		context[telemetry.ComponentKey] = c
	}
	if tick, ok := l.Attrs.Float(telemetry.LogTick); ok {
		context["tick"] = tick
	}
	if l.TraceID != "" {
		context[telemetry.TraceIDKey] = l.TraceID
	}
	if l.SpanID != "" {
		context[telemetry.SpanIDKey] = l.SpanID
	}
	fields := map[string]any{}
	for key, value := range l.Attrs {
		if name, ok := strings.CutPrefix(key, telemetry.FieldPrefix); ok {
			fields[name] = plainValue(value)
		}
	}
	payload := map[string]any{}
	if l.Attrs.Str(telemetry.LogRow) == telemetry.RowDecision {
		payload[telemetry.VerdictKey] = l.Attrs.Str(telemetry.LogVerdict)
		payload[telemetry.ReasonKey] = l.Attrs.Str(telemetry.LogReason)
		payload[telemetry.TargetKey] = l.Attrs.Str(telemetry.LogTarget)
		dur, _ := l.Attrs.Float(telemetry.LogDurMs)
		payload[telemetry.DurMsKey] = dur
		payload[telemetry.AttrsKey] = fields
	} else {
		for k, v := range fields {
			payload[k] = v
		}
		if l.Body != "" {
			payload["msg"] = l.Body
		}
	}
	if l.Attrs.Bool(telemetry.LogTruncated) {
		payload["truncated"] = true
		if n, ok := l.Attrs.Float(telemetry.LogOriginalBytes); ok {
			payload["original_bytes"] = n
		}
		payload["sha256"] = l.Attrs.Str(telemetry.LogSHA256)
		payload["preview"] = l.Attrs.Str(telemetry.LogPreview)
	}
	return TimelineRecord{
		Kind:     kind,
		Run:      l.Resource.Instance,
		WallTime: float64(l.Time.UnixNano()) / 1e9,
		Context:  context,
		Payload:  payload,
	}, true
}

// plainValue converts an OTLP attribute value to the JSON-decoded form rows
// hold: integers become float64, arrays []any and kvlists map[string]any.
func plainValue(v any) any {
	switch x := v.(type) {
	case int64:
		return float64(x)
	case []any:
		out := make([]any, len(x))
		for i, e := range x {
			out[i] = plainValue(e)
		}
		return out
	case map[string]any:
		out := make(map[string]any, len(x))
		for k, e := range x {
			out[k] = plainValue(e)
		}
		return out
	}
	return v
}
