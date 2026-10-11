// Package logview reads the diagnostic stream from the OTLP log ring
// (logs.jsonl, see docs/developers/contracts/otel-core.md and the OTEL mapping
// in otel-signals.md): it resolves the ring, filters its rows, collapses
// repeated decision rows into one counted entry and follows the ring across
// rotation. It is the reader behind the CLI (`rimgovernor log`).
package logview

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Path resolves the OTLP log ring: a profile directory's otlp/logs.jsonl, or
// the file path itself.
func Path(profile, file string) string {
	if profile != "" {
		return filepath.Join(profile, "otlp", otel.LogsFile)
	}
	return file
}

// levelRank orders the levels by severity; Filter.Level is a minimum.
var levelRank = map[string]int{"INFO": 0, "WARN": 1, "ERROR": 2}

// ValidLevel reports whether s names a level (case-insensitive).
func ValidLevel(s string) bool { _, ok := levelRank[strings.ToUpper(s)]; return ok }

// TickRange is an inclusive tick interval; a zero value matches everything.
type TickRange struct {
	Min, Max       int64
	HasMin, HasMax bool
}

// ParseTickRange reads "a..b", "a..", "..b" or "a" (one tick).
func ParseTickRange(s string) (TickRange, error) {
	var r TickRange
	lo, hi, found := strings.Cut(s, "..")
	if !found {
		hi = lo
	}
	var err error
	if lo != "" {
		if r.Min, err = strconv.ParseInt(strings.TrimSpace(lo), 10, 64); err != nil {
			return TickRange{}, fmt.Errorf("tick range %q: %w", s, err)
		}
		r.HasMin = true
	}
	if hi != "" {
		if r.Max, err = strconv.ParseInt(strings.TrimSpace(hi), 10, 64); err != nil {
			return TickRange{}, fmt.Errorf("tick range %q: %w", s, err)
		}
		r.HasMax = true
	}
	if r.HasMin && r.HasMax && r.Min > r.Max {
		return TickRange{}, fmt.Errorf("tick range %q: start after end", s)
	}
	return r, nil
}

func (r TickRange) active() bool { return r.HasMin || r.HasMax }

// Filter selects rows. Empty fields match everything.
type Filter struct {
	Kind      string
	Component string
	Level     string // minimum severity
	Ticks     TickRange
	Trace     string
	// SinceRun keeps only the newest launch's rows (the run of the last
	// row that names one).
	SinceRun bool
}

// Apply returns the rows f keeps, in order.
func (f Filter) Apply(records []bridge.TimelineRecord) []bridge.TimelineRecord {
	run := ""
	if f.SinceRun {
		for i := len(records) - 1; i >= 0; i-- {
			if records[i].Run != "" {
				run = records[i].Run
				break
			}
		}
	}
	var out []bridge.TimelineRecord
	for _, rec := range records {
		if f.match(rec, run) {
			out = append(out, rec)
		}
	}
	return out
}

func (f Filter) match(rec bridge.TimelineRecord, run string) bool {
	if f.Kind != "" && rec.Kind != f.Kind {
		return false
	}
	if f.Component != "" && contextString(rec, "component") != f.Component {
		return false
	}
	if f.Level != "" && levelRank[Level(rec)] < levelRank[strings.ToUpper(f.Level)] {
		return false
	}
	if f.Trace != "" && contextString(rec, "trace_id") != f.Trace {
		return false
	}
	if f.SinceRun && run != "" && rec.Run != run {
		return false
	}
	if f.Ticks.active() {
		tick, ok := Tick(rec)
		if !ok || (f.Ticks.HasMin && tick < f.Ticks.Min) || (f.Ticks.HasMax && tick > f.Ticks.Max) {
			return false
		}
	}
	return true
}

func contextString(rec bridge.TimelineRecord, key string) string {
	s, _ := rec.Context[key].(string)
	return s
}

// Level is the row's severity, INFO when it carries none.
func Level(rec bridge.TimelineRecord) string {
	if s := strings.ToUpper(contextString(rec, "level")); s != "" {
		return s
	}
	return "INFO"
}

// Tick is the tick the row was stamped with, when it has one.
func Tick(rec bridge.TimelineRecord) (int64, bool) {
	switch v := rec.Context["tick"].(type) {
	case float64:
		return int64(v), true
	case string:
		n, err := strconv.ParseInt(v, 10, 64)
		return n, err == nil
	}
	return 0, false
}

// Entry is one line of output: a row, or the run of decision rows that
// share (kind, component, verdict, reason, target).
type Entry struct {
	Kind      string         `json:"kind"`
	Component string         `json:"component,omitempty"`
	Level     string         `json:"level,omitempty"` // the highest seen
	Verdict   string         `json:"verdict,omitempty"`
	Reason    string         `json:"reason,omitempty"`
	Target    string         `json:"target,omitempty"`
	Count     int            `json:"count"`
	FirstTick *int64         `json:"first_tick,omitempty"`
	LastTick  *int64         `json:"last_tick,omitempty"`
	FirstAt   string         `json:"first_at,omitempty"`
	LastAt    string         `json:"last_at,omitempty"`
	Payload   map[string]any `json:"payload,omitempty"` // the latest occurrence's
	Decision  bool           `json:"decision"`
}

// IsDecision reports whether the row has the decision shape (a verdict key).
func IsDecision(rec bridge.TimelineRecord) bool {
	_, ok := rec.Payload[telemetry.VerdictKey]
	return ok
}

func payloadString(rec bridge.TimelineRecord, key string) string {
	s, _ := rec.Payload[key].(string)
	return s
}

// NewEntry is the single-row entry for rec.
func NewEntry(rec bridge.TimelineRecord) Entry {
	e := Entry{Kind: rec.Kind, Component: contextString(rec, "component"), Level: Level(rec), Count: 1,
		FirstAt: contextString(rec, "at"), LastAt: contextString(rec, "at"), Payload: rec.Payload}
	if tick, ok := Tick(rec); ok {
		e.FirstTick, e.LastTick = &tick, &tick
	}
	if IsDecision(rec) {
		e.Decision = true
		e.Verdict = payloadString(rec, telemetry.VerdictKey)
		e.Reason = payloadString(rec, telemetry.ReasonKey)
		e.Target = payloadString(rec, telemetry.TargetKey)
	}
	return e
}

func (e *Entry) absorb(rec bridge.TimelineRecord) {
	e.Count++
	e.Payload = rec.Payload
	if at := contextString(rec, "at"); at != "" {
		e.LastAt = at
	}
	if tick, ok := Tick(rec); ok {
		e.LastTick = &tick
		if e.FirstTick == nil {
			e.FirstTick = &tick
		}
	}
	if levelRank[Level(rec)] > levelRank[e.Level] {
		e.Level = Level(rec)
	}
}

// accessKind is httpapi.AccessKind; logview does not import the server.
const accessKind = "http_access"

// Collapse turns rows into entries, in order of first appearance. Decision
// rows with the same (kind, component, verdict, reason, target), and
// http_access rows with the same (component, level, method, path, status), become one
// entry carrying the count and the first and last tick, wherever they sit in
// the stream; every other row is its own entry.
func Collapse(records []bridge.TimelineRecord) []Entry {
	var entries []Entry
	index := map[[5]string]int{}
	for _, rec := range records {
		e := NewEntry(rec)
		key := [5]string{e.Kind, e.Component, e.Verdict, e.Reason, e.Target}
		switch {
		case e.Decision:
		case e.Kind == accessKind:
			// Repeated requests differ only in latency and size, so one
			// (method, path, status) is one entry however many times it fires.
			key = [5]string{e.Kind, e.Component, e.Level, payloadString(rec, "method") + " " + payloadString(rec, "path"), fmt.Sprint(rec.Payload["status"])}
		default:
			entries = append(entries, e)
			continue
		}
		if i, ok := index[key]; ok {
			entries[i].absorb(rec)
			continue
		}
		index[key] = len(entries)
		entries = append(entries, e)
	}
	return entries
}

// Line renders an entry as one text line, for example
// `tick 120 WARN  [clock-scheduler] admission refused critical_wave_budget window  x 40, first/last tick 120/2300`.
func (e Entry) Line() string {
	var b strings.Builder
	if e.FirstTick != nil {
		fmt.Fprintf(&b, "tick %d ", *e.FirstTick)
	} else {
		b.WriteString("tick - ")
	}
	fmt.Fprintf(&b, "%-5s ", e.Level)
	if e.Component != "" {
		fmt.Fprintf(&b, "[%s] ", e.Component)
	}
	b.WriteString(e.Words())
	if e.Count > 1 {
		fmt.Fprintf(&b, "  x %d", e.Count)
		if e.FirstTick != nil && e.LastTick != nil {
			fmt.Fprintf(&b, ", first/last tick %d/%d", *e.FirstTick, *e.LastTick)
		}
	}
	return b.String()
}

// Words is the entry's kind with what it says: a decision's verdict, reason
// and target (and its duration when it ran once), any other row's payload.
func (e Entry) Words() string {
	var b strings.Builder
	b.WriteString(e.Kind)
	if e.Decision {
		for _, part := range []string{e.Verdict, e.Reason, e.Target} {
			if part != "" {
				b.WriteString(" " + part)
			}
		}
		if ms, _ := e.Payload[telemetry.DurMsKey].(float64); ms > 0 && e.Count == 1 {
			fmt.Fprintf(&b, " %.1fms", ms)
		}
		if e.Kind == "build_stalled" {
			// A stalled room reads as its blueprint and frame counts and reasons.
			attrs, _ := e.Payload[telemetry.AttrsKey].(map[string]any)
			bp, _ := attrs["blueprints"].(float64)
			fr, _ := attrs["frames"].(float64)
			fmt.Fprintf(&b, " %.0f blueprints, %.0f frames", bp, fr)
			if reasons, ok := attrs["reasons"].(map[string]any); ok {
				keys := make([]string, 0, len(reasons))
				for k := range reasons {
					keys = append(keys, k)
				}
				sort.Strings(keys)
				for _, k := range keys {
					fmt.Fprintf(&b, " %s=%v", k, reasons[k])
				}
			}
		}
		if e.Kind == "food_credit" {
			// A credit row's point is the factor the ledger moved the channel to.
			attrs, _ := e.Payload[telemetry.AttrsKey].(map[string]any)
			if factor, ok := attrs["factor"].(float64); ok {
				fmt.Fprintf(&b, " factor %.2f", factor)
			}
			if state, ok := attrs["state"].(string); ok {
				b.WriteString(" (" + state + ")")
			}
		}
	} else if e.Kind == accessKind {
		method, _ := e.Payload["method"].(string)
		path, _ := e.Payload["path"].(string)
		fmt.Fprintf(&b, " %s %s %v", method, path, e.Payload["status"])
		if ms, _ := e.Payload["dur_ms"].(float64); ms > 0 && e.Count == 1 {
			fmt.Fprintf(&b, " %.1fms", ms)
		}
	} else if text := compactPayload(e.Payload); text != "" {
		b.WriteString(" " + text)
	}
	return b.String()
}

const payloadPreview = 200

var leadKeys = []string{"msg", "error", "reason", "method", "path", "status"}

// leadKeyRank is a key's position among leadKeys, after them all otherwise.
func leadKeyRank(key string) int {
	for i, k := range leadKeys {
		if k == key {
			return i
		}
	}
	return len(leadKeys)
}

func compactPayload(payload map[string]any) string {
	if len(payload) == 0 {
		return ""
	}
	keys := make([]string, 0, len(payload))
	for k := range payload {
		keys = append(keys, k)
	}
	// The keys that say what happened lead, so the preview's cut-off only ever
	// drops bookkeeping (bytes, seq, source).
	sort.Slice(keys, func(i, j int) bool {
		ri, rj := leadKeyRank(keys[i]), leadKeyRank(keys[j])
		if ri != rj {
			return ri < rj
		}
		return keys[i] < keys[j]
	})
	parts := make([]string, 0, len(keys))
	for _, k := range keys {
		v, _ := json.Marshal(payload[k])
		parts = append(parts, k+"="+string(v))
	}
	text := strings.Join(parts, " ")
	if len(text) > payloadPreview {
		text = text[:payloadPreview] + "..."
	}
	return text
}
