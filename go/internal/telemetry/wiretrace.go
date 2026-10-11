package telemetry

import "strings"

// The row context keys a record's trace ids are read back under
// (bridge.RecordFromLog) and that mod_log rows are written with.
const (
	TraceIDKey = "trace_id"
	SpanIDKey  = "span_id"
)

// ParseWireTrace splits what a companion log line or timing block carries:
// a W3C traceparent ("00-<trace>-<span>-<flags>") or the older
// "<trace>/<span>". It reports false for anything else.
func ParseWireTrace(wire string) (traceID, spanID string, ok bool) {
	if parts := strings.Split(wire, "-"); len(parts) == 4 && len(parts[1]) == 32 && len(parts[2]) == 16 {
		return parts[1], parts[2], true
	}
	if t, s, cut := strings.Cut(wire, "/"); cut && t != "" && s != "" {
		return t, s, true
	}
	return "", "", false
}
