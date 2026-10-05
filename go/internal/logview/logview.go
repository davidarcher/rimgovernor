// Package logview reads the diagnostic stream (flight.jsonl, schema v2, see
// docs/developers/contracts/flight-rows.md): it resolves the recording,
// filters its rows and collapses repeated decision rows into one counted
// entry. It is the single reader the CLI (`rimgovernor log`) and the other
// consumers of the stream share.
package logview

import (
	"context"
	"encoding/json"
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Path resolves a recording: a profile directory's flight/flight.jsonl, or
// the file path itself.
func Path(profile, file string) string {
	if profile != "" {
		return filepath.Join(profile, "flight", "flight.jsonl")
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

// Filter selects rows. Empty fields match everything. A recording_gap row
// always passes: it is the signal that the rows around it are incomplete.
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
	if rec.Kind == "recording_gap" {
		return true
	}
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
	if rec.Kind == "recording_gap" {
		e.Level = "WARN"
		e.Payload = map[string]any{"reason": rec.Reason, "file": rec.File, "line": rec.Line, "before": rec.Before, "after": rec.After}
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

// Collapse turns rows into entries, in order of first appearance. Decision
// rows with the same (kind, component, verdict, reason, target) become one
// entry carrying the count and the first and last tick, wherever they sit in
// the stream; every other row is its own entry.
func Collapse(records []bridge.TimelineRecord) []Entry {
	var entries []Entry
	index := map[[5]string]int{}
	for _, rec := range records {
		e := NewEntry(rec)
		if !e.Decision {
			entries = append(entries, e)
			continue
		}
		key := [5]string{e.Kind, e.Component, e.Verdict, e.Reason, e.Target}
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
	} else if text := compactPayload(e.Payload); text != "" {
		b.WriteString(" " + text)
	}
	return b.String()
}

const payloadPreview = 200

func compactPayload(payload map[string]any) string {
	if len(payload) == 0 {
		return ""
	}
	keys := make([]string, 0, len(payload))
	for k := range payload {
		keys = append(keys, k)
	}
	sort.Strings(keys)
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

// Follow calls emit with each row appended to the recording after the call
// starts that passes f (with the recording_gap before it, if any), polling every interval
// until ctx ends. Rows already present are skipped: the caller prints those
// first. Follow does not collapse.
func Follow(ctx context.Context, path string, f Filter, interval time.Duration, emit func(bridge.TimelineRecord)) error {
	reader := bridge.NewTimelineReader(path)
	var last uint64
	started := false
	for {
		records, err := reader.Read()
		if err != nil {
			return err
		}
		var fresh []bridge.TimelineRecord
		var gap *bridge.TimelineRecord // the gap just before the row being read
		for i, rec := range records {
			if rec.Kind == "recording_gap" {
				gap = &records[i]
				continue
			}
			if rec.HasSeq && rec.Sequence > last {
				last = rec.Sequence
				if started {
					if gap != nil {
						fresh = append(fresh, *gap)
					}
					fresh = append(fresh, rec)
				}
			}
			gap = nil
		}
		started = true
		for _, rec := range f.Apply(fresh) {
			emit(rec)
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(interval):
		}
	}
}
