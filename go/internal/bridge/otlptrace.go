package bridge

import (
	"fmt"
	"io"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Traces over OTLP. A trace is the spans of traces.jsonl that share a trace id
// (docs/developers/contracts/otel-core.md "Spans") joined with the log rows of
// logs.jsonl stamped with the same id. Spans export when they end, so a call
// that never returned has no native.call span: its native_request marker row
// is what is left, and the report says "(no reply recorded)" for it.

// TraceSummary is one trace: its finished spans and its log rows.
type TraceSummary struct {
	TraceID string
	Spans   []otel.Span
	Rows    []TimelineRecord
	// Root names the trace: its root span (with the attributes that say what
	// the step or dispatch was), else its last row's kind.
	Root string
	// Tick is the first tick a row carried, when any did.
	Tick    int64
	HasTick bool

	start, end time.Time
}

// Start is the trace's first span start or row time.
func (t TraceSummary) Start() time.Time { return t.start }

// SpanMs is the trace's wall time: the extent of its spans and rows.
func (t TraceSummary) SpanMs() float64 {
	return float64(t.end.Sub(t.start)) / float64(time.Millisecond)
}

// ReadTraces reads the traces beside the logs ring at logs (traces.jsonl in the
// same directory), oldest first. A missing ring is empty.
func ReadTraces(logs string) ([]TraceSummary, error) {
	rows, err := ReadOTLPTimeline(logs)
	if err != nil {
		return nil, err
	}
	var spans []otel.Span
	if err := otel.ReadSpans(otel.SiblingRing(logs, otel.TracesFile), func(s otel.Span) error {
		spans = append(spans, s)
		return nil
	}); err != nil {
		return nil, err
	}
	return SummarizeTraces(spans, rows), nil
}

// SummarizeTraces groups spans and rows by trace id, ordered by start. Rows
// without a trace id are left out.
func SummarizeTraces(spans []otel.Span, rows []TimelineRecord) []TraceSummary {
	index := map[string]*TraceSummary{}
	var order []*TraceSummary
	get := func(id string) *TraceSummary {
		if t := index[id]; t != nil {
			return t
		}
		t := &TraceSummary{TraceID: id}
		index[id] = t
		order = append(order, t)
		return t
	}
	widen := func(t *TraceSummary, from, to time.Time) {
		if t.start.IsZero() || from.Before(t.start) {
			t.start = from
		}
		if to.After(t.end) {
			t.end = to
		}
	}
	for _, s := range spans {
		t := get(s.TraceID)
		t.Spans = append(t.Spans, s)
		widen(t, s.Start, s.End)
	}
	for _, row := range rows {
		id, _ := row.Context[telemetry.TraceIDKey].(string)
		if id == "" {
			continue
		}
		t := get(id)
		t.Rows = append(t.Rows, row)
		at := rowTime(row)
		widen(t, at, at)
		if !t.HasTick {
			if tick, ok := tickValue(row.Context["tick"]); ok {
				t.Tick, t.HasTick = tick, true
			}
		}
	}
	out := make([]TraceSummary, 0, len(order))
	for _, t := range order {
		sort.SliceStable(t.Spans, func(i, j int) bool { return t.Spans[i].Start.Before(t.Spans[j].Start) })
		if root := rootSpan(t.Spans); root != nil {
			t.Root = spanLabel(*root)
		} else if len(t.Rows) > 0 {
			t.Root = t.Rows[len(t.Rows)-1].Kind
		}
		out = append(out, *t)
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].start.Before(out[j].start) })
	return out
}

// FindTrace is the trace with id, false when no span or row carries it.
func FindTrace(traces []TraceSummary, id string) (TraceSummary, bool) {
	for _, t := range traces {
		if t.TraceID == id {
			return t, true
		}
	}
	return TraceSummary{}, false
}

func rowTime(row TimelineRecord) time.Time {
	return time.Unix(0, int64(row.WallTime*1e9))
}

// rootSpan is the earliest span whose parent is absent from spans.
func rootSpan(spans []otel.Span) *otel.Span {
	known := map[string]bool{}
	for _, s := range spans {
		known[s.SpanID] = true
	}
	for i := range spans {
		if spans[i].ParentSpanID == "" || !known[spans[i].ParentSpanID] {
			return &spans[i]
		}
	}
	return nil
}

const spanAttrPrefix = "rimgovernor."

// spanLabel is a span's name with the attributes that say what it was.
func spanLabel(s otel.Span) string {
	label := s.Name
	for _, key := range []string{"step_cause", "action_kind", "concern", "tool"} {
		if v := s.Attrs.Str(spanAttrPrefix + key); v != "" {
			label += " " + key + "=" + v
		}
	}
	return label
}

// WriteTraceIndex lists traces one per line, oldest first, with the offset from
// the first trace, so a reader can pick the step that took 4 s and render it
// with WriteTraceReport. Single-item traces (a lone native read, a row logged
// outside any unit of work) are counted, not listed.
func WriteTraceIndex(w io.Writer, traces []TraceSummary) {
	if len(traces) == 0 {
		fmt.Fprintln(w, "no traces recorded")
		return
	}
	origin := traces[0].Start()
	fmt.Fprintf(w, "%-32s %10s %9s %5s %5s %8s  %s\n", "trace", "at ms", "span ms", "spans", "rows", "tick", "root")
	singles := 0
	for _, t := range traces {
		if len(t.Spans)+len(t.Rows) == 1 {
			singles++
			continue
		}
		tick := "-"
		if t.HasTick {
			tick = fmt.Sprint(t.Tick)
		}
		fmt.Fprintf(w, "%-32s %10.1f %9.1f %5d %5d %8s  %s\n", t.TraceID, float64(t.Start().Sub(origin))/float64(time.Millisecond), t.SpanMs(), len(t.Spans), len(t.Rows), tick, t.Root)
	}
	if singles > 0 {
		fmt.Fprintf(w, "%d single-item trace(s) not listed\n", singles)
	}
}

// traceLine is one rendered line of a trace.
type traceLine struct {
	offsetMs   float64
	durationMs float64
	timed      bool
	span       string
	depth      int
	text       string
}

// WriteTraceReport renders one trace as a tree: each span with its offset from
// the trace start, duration, status and attributes, and under it the rows
// logged inside it (decisions, events, frame hits) in time order. A completed
// native call is its native.call span; a native_request marker with no
// answering native_call row is a call that never returned.
func WriteTraceReport(w io.Writer, t TraceSummary) {
	tick := "-"
	if t.HasTick {
		tick = fmt.Sprint(t.Tick)
	}
	fmt.Fprintf(w, "trace %s: %d spans, %d rows over %.1fms, tick %s\n", t.TraceID, len(t.Spans), len(t.Rows), t.SpanMs(), tick)
	fmt.Fprintf(w, "%9s %8s  %-8s  %s\n", "at ms", "dur ms", "span", "what")
	for _, line := range traceLines(t) {
		duration := "-"
		if line.timed {
			duration = fmt.Sprintf("%.1f", line.durationMs)
		}
		fmt.Fprintf(w, "%9.1f %8s  %-8s  %s%s\n", line.offsetMs, duration, line.span, strings.Repeat("  ", line.depth), line.text)
	}
}

type traceItem struct {
	at   time.Time
	span *otel.Span
	row  *TimelineRecord
}

// answeredRequests are the request ids native_call rows answer.
func answeredRequests(rows []TimelineRecord) map[float64]bool {
	answered := map[float64]bool{}
	for _, row := range rows {
		if IsNativeReply(row.Kind) {
			if id, ok := number(row.Payload["request"]); ok {
				answered[id] = true
			}
		}
	}
	return answered
}

// unanswered reports whether row is a native_request marker no reply answers.
func unanswered(row TimelineRecord, answered map[float64]bool) bool {
	if row.Kind != "native_request" {
		return false
	}
	id, ok := number(row.Payload["request"])
	return !(ok && answered[id])
}

func traceLines(t TraceSummary) []traceLine {
	inTrace := map[string]bool{}
	for _, s := range t.Spans {
		inTrace[s.SpanID] = true
	}
	answered := answeredRequests(t.Rows)
	// Items by parent span; a parent outside the trace (an open span, or a
	// trace that begins in another process) is the top level, "".
	children := map[string][]traceItem{}
	for i := range t.Spans {
		s := &t.Spans[i]
		parent := s.ParentSpanID
		if !inTrace[parent] {
			parent = ""
		}
		children[parent] = append(children[parent], traceItem{at: s.Start, span: s})
	}
	for i := range t.Rows {
		row := &t.Rows[i]
		if IsNativeReply(row.Kind) || (row.Kind == "native_request" && !unanswered(*row, answered)) {
			continue // the native.call span carries a completed call
		}
		parent, _ := row.Context[telemetry.SpanIDKey].(string)
		if !inTrace[parent] {
			parent = ""
		}
		children[parent] = append(children[parent], traceItem{at: rowTime(*row), row: row})
	}
	for _, items := range children {
		sort.SliceStable(items, func(i, j int) bool { return items[i].at.Before(items[j].at) })
	}
	var out []traceLine
	var walk func(parent string, depth int)
	walk = func(parent string, depth int) {
		for _, item := range children[parent] {
			line := traceLine{offsetMs: float64(item.at.Sub(t.start)) / float64(time.Millisecond), depth: depth}
			if s := item.span; s != nil {
				line.span, line.timed, line.durationMs = shortID(s.SpanID), true, float64(s.Duration())/float64(time.Millisecond)
				line.text = spanText(*s)
				out = append(out, line)
				walk(s.SpanID, depth+1)
				continue
			}
			span, _ := item.row.Context[telemetry.SpanIDKey].(string)
			line.span, line.text = shortID(span), rowText(*item.row)
			out = append(out, line)
		}
	}
	walk("", 0)
	return out
}

func spanText(s otel.Span) string {
	text := s.Name + attrText(s.Attrs)
	if s.StatusCode == otel.StatusError {
		text += "  ERROR"
		if s.StatusMsg != "" {
			text += ": " + s.StatusMsg
		}
	}
	return text
}

func rowText(row TimelineRecord) string {
	switch row.Kind {
	case "native_request":
		return "native " + toolName(row.Payload) + "  (no reply recorded)"
	case "native_frame":
		switch outcome, _ := row.Payload["outcome"].(string); outcome {
		case "hit":
			return "frame hit " + toolName(row.Payload)
		case "miss":
			text := "frame miss " + toolName(row.Payload)
			if why, _ := row.Payload["why"].(string); why != "" {
				text += " (" + why + ")"
			}
			return text
		}
	}
	if IsDecisionRow(row) {
		attrs, _ := row.Payload[telemetry.AttrsKey].(map[string]any)
		return row.Kind + decisionWords(row) + mapText(attrs)
	}
	text := row.Kind
	if msg, _ := row.Payload["msg"].(string); msg != "" {
		text += " " + fmt.Sprintf("%q", msg)
	}
	return text + mapText(row.Payload)
}

// attrText renders span attributes as " k=v" in key order, without the
// rimgovernor. prefix; floats print to a tenth.
func attrText(attrs otel.Attrs) string {
	keys := make([]string, 0, len(attrs))
	for key := range attrs {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	var b strings.Builder
	for _, key := range keys {
		name := strings.TrimPrefix(key, spanAttrPrefix)
		if v, ok := attrs[key].(float64); ok {
			fmt.Fprintf(&b, " %s=%.1f", name, v)
		} else {
			fmt.Fprintf(&b, " %s=%v", name, attrs[key])
		}
	}
	return b.String()
}

// mapText renders a kinded row's attributes (all but msg) as " k=v" in key order.
func mapText(payload map[string]any) string {
	keys := make([]string, 0, len(payload))
	for key := range payload {
		if key != "msg" {
			keys = append(keys, key)
		}
	}
	sort.Strings(keys)
	var b strings.Builder
	for _, key := range keys {
		fmt.Fprintf(&b, " %s=%v", key, payload[key])
	}
	return b.String()
}

func toolName(payload map[string]any) string {
	if native, _ := payload["native_tool"].(string); native != "" {
		return native
	}
	tool, _ := payload["tool"].(string)
	return tool
}

func shortID(id string) string {
	if len(id) > 8 {
		return id[:8]
	}
	return id
}

// decisionWords is a decision row's verdict, reason and target, each preceded by a space.
func decisionWords(row TimelineRecord) string {
	var b strings.Builder
	for _, key := range []string{telemetry.VerdictKey, telemetry.ReasonKey, telemetry.TargetKey} {
		if s, _ := row.Payload[key].(string); s != "" {
			b.WriteString(" " + s)
		}
	}
	return b.String()
}

// TraceJSON is the jq-friendly form of one trace: spans (start as Unix
// seconds, durations in ms) and rows.
type TraceJSON struct {
	TraceID string     `json:"trace_id"`
	Tick    *int64     `json:"tick,omitempty"`
	Spans   []SpanJSON `json:"spans"`
	Rows    []RowJSON  `json:"rows"`
}

// SpanJSON is one span in TraceJSON.
type SpanJSON struct {
	SpanID        string         `json:"span_id"`
	ParentID      string         `json:"parent_id,omitempty"`
	Name          string         `json:"name"`
	Start         float64        `json:"start"`
	DurMs         float64        `json:"dur_ms"`
	Error         bool           `json:"error,omitempty"`
	StatusMessage string         `json:"status_message,omitempty"`
	Attrs         map[string]any `json:"attrs,omitempty"`
}

// RowJSON is one log row in TraceJSON. NoReply marks a native_request marker
// no native_call answered: a call with no reply recorded.
type RowJSON struct {
	Kind    string         `json:"kind"`
	SpanID  string         `json:"span_id,omitempty"`
	Time    float64        `json:"time"`
	NoReply bool           `json:"no_reply,omitempty"`
	Context map[string]any `json:"context"`
	Payload map[string]any `json:"payload"`
}

// JSON is t as TraceJSON.
func (t TraceSummary) JSON() TraceJSON {
	out := TraceJSON{TraceID: t.TraceID, Spans: []SpanJSON{}, Rows: []RowJSON{}}
	if t.HasTick {
		out.Tick = &t.Tick
	}
	for _, s := range t.Spans {
		out.Spans = append(out.Spans, SpanJSON{SpanID: s.SpanID, ParentID: s.ParentSpanID, Name: s.Name, Start: float64(s.Start.UnixNano()) / 1e9,
			DurMs: float64(s.Duration()) / float64(time.Millisecond), Error: s.StatusCode == otel.StatusError, StatusMessage: s.StatusMsg, Attrs: s.Attrs})
	}
	answered := answeredRequests(t.Rows)
	for _, row := range t.Rows {
		span, _ := row.Context[telemetry.SpanIDKey].(string)
		out.Rows = append(out.Rows, RowJSON{Kind: row.Kind, SpanID: span, Time: row.WallTime, NoReply: unanswered(row, answered), Context: row.Context, Payload: row.Payload})
	}
	return out
}
