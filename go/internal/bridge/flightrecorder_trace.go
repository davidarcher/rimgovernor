package bridge

import (
	"fmt"
	"io"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// TraceSummary is one trace in a recording: the rows that share a
// trace_id (a scheduler step and the Worker dispatches nested under it, a
// poll, a renewal), in sequence order.
type TraceSummary struct {
	TraceID string
	Rows    []TimelineRecord
	// Kinds counts the rows by kind.
	Kinds map[string]int
	// Root names the trace: the message of its last scheduler_step or
	// worker_outcome row, else the kind of its last row.
	Root string
	// Ticks is the first observed tick a row carried, when any did.
	Tick    int64
	HasTick bool
}

// Start is the first row's wall time in seconds.
func (t TraceSummary) Start() float64 { return t.Rows[0].WallTime }

// SpanMs is the wall time from the first row to the last.
func (t TraceSummary) SpanMs() float64 {
	return (t.Rows[len(t.Rows)-1].WallTime - t.Rows[0].WallTime) * 1000
}

// SummarizeTraces groups a recording's rows by trace_id, in order of first
// appearance. Rows without one (a recording that predates #298) are left
// out; ReadTimeline's gap records too.
func SummarizeTraces(records []TimelineRecord) []TraceSummary {
	var out []TraceSummary
	index := map[string]int{}
	for _, row := range records {
		id, _ := row.Context[telemetry.TraceIDKey].(string)
		if id == "" {
			continue
		}
		i, seen := index[id]
		if !seen {
			i = len(out)
			index[id] = i
			out = append(out, TraceSummary{TraceID: id, Kinds: map[string]int{}})
		}
		t := &out[i]
		t.Rows = append(t.Rows, row)
		t.Kinds[row.Kind]++
		if !t.HasTick {
			if tick, ok := tickValue(row.Context["tick"]); ok {
				t.Tick, t.HasTick = tick, true
			}
		}
	}
	for i := range out {
		out[i].Root = traceRoot(out[i].Rows)
	}
	return out
}

// traceRoot names a trace by what it was: the last scheduler_step
// message (the step's outcome), else the last worker_outcome, else the
// last other kinded row, else the native tool it called.
func traceRoot(rows []TimelineRecord) string {
	for _, kind := range []string{"scheduler_step", "worker_outcome"} {
		for i := len(rows) - 1; i >= 0; i-- {
			if rows[i].Kind == kind {
				if msg, _ := rows[i].Payload["msg"].(string); msg != "" {
					return kind + ": " + msg
				}
				return kind
			}
		}
	}
	for i := len(rows) - 1; i >= 0; i-- {
		if !strings.HasPrefix(rows[i].Kind, "native_") {
			return rows[i].Kind
		}
	}
	for _, row := range rows {
		if tool := toolName(row.Payload); tool != "" && row.Kind != "native_request" {
			return "native " + tool
		}
	}
	return rows[len(rows)-1].Kind
}

// FindTrace is the summary of one trace, or false when the recording has
// no row under that id.
func FindTrace(records []TimelineRecord, traceID string) (TraceSummary, bool) {
	for _, t := range SummarizeTraces(records) {
		if t.TraceID == traceID {
			return t, true
		}
	}
	return TraceSummary{}, false
}

// WriteTraceIndex lists a recording's traces one per line, oldest first,
// with the offset from the recording's first traced row, so a reader can
// pick the step that took 4 s and render it with WriteTraceReport. The
// single-row traces (the coverage row, tallies written without a ctx) are
// counted, not listed.
func WriteTraceIndex(w io.Writer, traces []TraceSummary) {
	if len(traces) == 0 {
		fmt.Fprintln(w, "no traced rows (recorded before #298?)")
		return
	}
	origin := traces[0].Start()
	fmt.Fprintf(w, "%-16s %10s %9s %5s %8s  %s\n", "trace", "at ms", "span ms", "rows", "tick", "root")
	singles := 0
	for _, t := range traces {
		if len(t.Rows) == 1 {
			singles++
			continue
		}
		tick := "-"
		if t.HasTick {
			tick = fmt.Sprint(t.Tick)
		}
		fmt.Fprintf(w, "%-16s %10.1f %9.1f %5d %8s  %s\n", t.TraceID, (t.Start()-origin)*1000, t.SpanMs(), len(t.Rows), tick, t.Root)
	}
	if singles > 0 {
		fmt.Fprintf(w, "%d single-row trace(s) not listed\n", singles)
	}
}

// traceLine is one rendered row of a trace: a native call is one line
// (its request row, with the phases its response or error row reported),
// every other row is itself.
type traceLine struct {
	offsetMs   float64
	durationMs float64
	timed      bool
	span       string
	depth      int
	text       string
}

// WriteTraceReport renders one trace as a waterfall: each row's offset from
// the trace's first row, the call's duration for a native call, the span it
// ran under (indented by nesting under the root), and what it was: the
// native tool and its phases (gate wait, GABS round trip, the companion's
// queue and execute split when echoed), a cache hit, a step's read tally,
// or a kinded service event with its message and attributes.
func WriteTraceReport(w io.Writer, t TraceSummary) {
	lines := traceLines(t)
	tick := "-"
	if t.HasTick {
		tick = fmt.Sprint(t.Tick)
	}
	fmt.Fprintf(w, "trace %s: %d rows over %.1fms, tick %s, sequence %d..%d\n", t.TraceID, len(t.Rows), t.SpanMs(), tick, t.Rows[0].Sequence, t.Rows[len(t.Rows)-1].Sequence)
	fmt.Fprintf(w, "%9s %8s  %-8s  %s\n", "at ms", "dur ms", "span", "row")
	for _, line := range lines {
		duration := "-"
		if line.timed {
			duration = fmt.Sprintf("%.1f", line.durationMs)
		}
		fmt.Fprintf(w, "%9.1f %8s  %-8s  %s%s\n", line.offsetMs, duration, line.span, strings.Repeat("  ", line.depth), line.text)
	}
}

func traceLines(t TraceSummary) []traceLine {
	// A response or error row names its request by sequence; join them so
	// the call reads as one line with its phases.
	replies := map[uint64]TimelineRecord{}
	for _, row := range t.Rows {
		if row.Kind == "native_response" || row.Kind == "native_error" {
			if seq, ok := number(row.Payload["request"]); ok {
				replies[uint64(seq)] = row
			}
		}
	}
	depths := traceDepths(t)
	origin := t.Rows[0].WallTime
	var out []traceLine
	for _, row := range t.Rows {
		span, _ := row.Context[telemetry.SpanIDKey].(string)
		line := traceLine{offsetMs: (row.WallTime - origin) * 1000, span: shortID(span), depth: depths[span]}
		switch row.Kind {
		case "native_response", "native_error":
			if _, joined := number(row.Payload["request"]); joined {
				continue
			}
			line.text = row.Kind + " " + toolName(row.Payload)
		case "native_request":
			reply, replied := replies[row.Sequence]
			if !replied {
				line.text = "native " + toolName(row.Payload) + "  (no reply recorded)"
				break
			}
			// The reply names the inner rimgovernor/* tool; the request
			// row only carries the GABS wrapper and its arguments. A call
			// through another wrapper (games_tool_detail describing the
			// tool before its first use) keeps the wrapper's name.
			line.text = "native " + toolName(reply.Payload)
			if wrapper, _ := reply.Payload["tool"].(string); wrapper != "" && wrapper != "games_call_tool" {
				line.text = wrapper + " " + toolName(reply.Payload)
			}
			if timing, ok := reply.Payload["timing"].(map[string]any); ok {
				if total, ok := number(timing["total_ms"]); ok {
					line.durationMs, line.timed = total, true
				}
				line.text += fmt.Sprintf("  gate %.1f call %.1f decode %.1f", field(timing, "gate_wait_ms"), field(timing, "call_ms"), field(timing, "decode_ms"))
				if queue, ok := number(timing["native_queue_ms"]); ok {
					line.text += fmt.Sprintf(" native queue %.1f exec %.1f", queue, field(timing, "native_execute_ms"))
				}
				if echoed, _ := timing["native_trace"].(string); echoed != "" && echoed != t.TraceID+"/"+span {
					line.text += " echoed " + echoed
				}
			}
			if reply.Kind == "native_error" {
				if text, _ := reply.Payload["error"].(string); text != "" {
					line.text += "  error: " + text
				}
			}
		case "native_cache_hit":
			line.text = "cache hit " + toolName(row.Payload)
		case "native_decode":
			continue
		default:
			line.text = row.Kind
			if msg, _ := row.Payload["msg"].(string); msg != "" {
				line.text += " " + fmt.Sprintf("%q", msg)
			}
			line.text += payloadAttrs(row.Payload)
		}
		out = append(out, line)
	}
	return out
}

// traceDepths is each span's nesting under the trace's root, by following
// parent_id; a span whose parent is not in the trace sits at depth 0.
func traceDepths(t TraceSummary) map[string]int {
	parents := map[string]string{}
	for _, row := range t.Rows {
		span, _ := row.Context[telemetry.SpanIDKey].(string)
		parent, _ := row.Context[telemetry.ParentIDKey].(string)
		if span != "" {
			parents[span] = parent
		}
	}
	depths := map[string]int{}
	for span := range parents {
		depth := 0
		for cursor := parents[span]; cursor != "" && depth < 16; cursor = parents[cursor] {
			if _, known := parents[cursor]; !known {
				break
			}
			depth++
		}
		depths[span] = depth
	}
	return depths
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

// payloadAttrs renders a kinded row's attributes (all but msg) as k=v in
// key order; nested values print as JSON-ish text.
func payloadAttrs(payload map[string]any) string {
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
