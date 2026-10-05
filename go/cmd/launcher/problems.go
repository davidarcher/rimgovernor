package main

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// The Problems tab's recorder half (#1987): the flight recorder under the
// profile read in-process, so the tab still shows the last session after
// the controller crashed or stopped.

const (
	problemsFeedLength = 200 // newest matching rows shown
	searchTextLimit    = 16384
)

// hiddenByDefault are the kinds the feed hides until asked. None: the rows that
// shadowed another (native_decode) are folded into native_call.
var hiddenByDefault = []string{}

// ProblemEvent is one recorder row of the feed.
type ProblemEvent struct {
	Seq     uint64 // 0 on a recording_gap row
	Kind    string
	Wall    float64 // unix seconds
	Tick    int64
	HasTick bool
	Summary string
	Trace   string
	Problem bool   // a native error, a recording gap, or a WARN/ERROR log row
	Text    string // the row as the Copy buttons put it on the clipboard
}

// KindCount is one entry of the kind filter.
type KindCount struct {
	Kind  string
	Count int
}

// ProblemHealth is the strip computed from the newest run's rows.
type ProblemHealth struct {
	Known         bool
	Run           string
	Tick          int64
	HasTick       bool
	TPS           float64
	LastStepMs    float64
	NativeErrors  float64
	NativeCalls   float64
	ReadsPerStep  float64
	Authority     string // "native generation N", or "" when no authority_change row
	CacheHitRatio float64
}

// ProblemsView is what the Problems tab polls.
type ProblemsView struct {
	Available     bool   // the recorder file exists and holds rows
	Empty         string // why not, when not Available
	Path          string
	Rows          int // rows held
	Events        []ProblemEvent
	Kinds         []KindCount
	Health        ProblemHealth
	DefaultHidden []string
}

// recorderTail follows one flight recorder ring, decoding only what was
// appended since the last call (bridge.TimelineReader).
type recorderTail struct {
	mu     sync.Mutex
	path   string
	reader *bridge.TimelineReader

	healthKey string // rows held and newest sequence the cached health was computed at
	health    ProblemHealth
}

func newRecorderTail(path string) *recorderTail {
	return &recorderTail{path: path, reader: bridge.NewTimelineReader(path)}
}

// read returns the ring's rows, oldest first.
func (t *recorderTail) read() ([]bridge.TimelineRecord, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.reader.Read()
}

// view answers a poll: the newest-first feed without the hidden kinds and
// matching needle (a case-insensitive substring of the row's context and
// payload, or its trace id), the kind filter's counts and the health strip.
func (t *recorderTail) view(hidden []string, needle string) ProblemsView {
	out := ProblemsView{Path: t.path, Events: []ProblemEvent{}, Kinds: []KindCount{}, DefaultHidden: hiddenByDefault}
	rows, err := t.read()
	if err != nil {
		out.Empty = "The flight recorder could not be read: " + err.Error()
		return out
	}
	if len(rows) == 0 {
		if _, statErr := os.Stat(t.path); statErr != nil {
			out.Empty = "No flight recorder yet. It appears at " + t.path + " once the controller has run."
		} else {
			out.Empty = "The flight recorder is empty."
		}
		return out
	}
	out.Available, out.Rows = true, len(rows)
	hide := map[string]bool{}
	for _, k := range hidden {
		hide[k] = true
	}
	needle = strings.ToLower(strings.TrimSpace(needle))
	counts := map[string]int{}
	for _, r := range rows {
		counts[r.Kind]++
	}
	for k, n := range counts {
		out.Kinds = append(out.Kinds, KindCount{k, n})
	}
	sort.Slice(out.Kinds, func(i, j int) bool { return out.Kinds[i].Kind < out.Kinds[j].Kind })
	for i := len(rows) - 1; i >= 0 && len(out.Events) < problemsFeedLength; i-- {
		r := rows[i]
		if hide[r.Kind] || (needle != "" && !rowMatches(r, needle)) {
			continue
		}
		out.Events = append(out.Events, problemEvent(r))
	}
	out.Health = t.healthOf(rows)
	return out
}

// allProblems is the text of every problem row, oldest first, whatever the
// feed's filters.
func (t *recorderTail) allProblems() string {
	rows, err := t.read()
	if err != nil {
		return ""
	}
	var parts []string
	for _, r := range rows {
		if e := problemEvent(r); e.Problem {
			parts = append(parts, e.Text)
		}
	}
	return strings.Join(parts, "\n\n")
}

func (t *recorderTail) healthOf(rows []bridge.TimelineRecord) ProblemHealth {
	last := rows[len(rows)-1]
	key := fmt.Sprintf("%d/%d/%g", len(rows), last.Sequence, last.WallTime)
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.healthKey == key {
		return t.health
	}
	t.healthKey, t.health = key, computeHealth(rows)
	return t.health
}

// currentRun is the rows of the newest recorder run: a ring outlives the
// process that wrote it, so successive launches share one file.
func currentRun(rows []bridge.TimelineRecord) []bridge.TimelineRecord {
	run := ""
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].HasSeq {
			run = rows[i].Run
			break
		}
	}
	var out []bridge.TimelineRecord
	for _, r := range rows {
		if r.HasSeq && r.Run == run {
			out = append(out, r)
		}
	}
	return out
}

func computeHealth(rows []bridge.TimelineRecord) ProblemHealth {
	current := currentRun(rows)
	if len(current) == 0 {
		return ProblemHealth{}
	}
	m := na.RecorderMetrics(current)
	h := ProblemHealth{
		Known: true, Run: current[0].Run, TPS: m["wall_tps"], NativeErrors: m["native_errors"], NativeCalls: m["native_calls"],
		ReadsPerStep: m["reads_per_step_mean"], CacheHitRatio: m["cache_hit_ratio"],
	}
	gotTick, gotStep, gotAuthority := false, false, false
	for i := len(current) - 1; i >= 0 && !(gotTick && gotStep && gotAuthority); i-- {
		r := current[i]
		if !gotTick {
			if tick, ok := tickOf(r); ok {
				h.Tick, h.HasTick, gotTick = tick, true, true
			}
		}
		if !gotStep && r.Kind == "clock_step" {
			if ms, ok := bridge.StepFields(r)["elapsed_ms"].(float64); ok {
				h.LastStepMs, gotStep = ms, true
			}
		}
		if !gotAuthority && bridge.IsAuthorityKind(r.Kind) {
			if g, ok := r.Payload["generation"]; ok {
				h.Authority, gotAuthority = "native generation "+scalarText(g), true
			}
		}
	}
	return h
}

func tickOf(r bridge.TimelineRecord) (int64, bool) {
	switch v := r.Context["tick"].(type) {
	case float64:
		return int64(v), true
	case int64:
		return v, true
	}
	return 0, false
}

func traceOf(r bridge.TimelineRecord) string {
	id, _ := r.Context["trace_id"].(string)
	return id
}

func rowMatches(r bridge.TimelineRecord, needle string) bool {
	if strings.Contains(strings.ToLower(traceOf(r)), needle) || strings.Contains(strings.ToLower(r.Kind), needle) {
		return true
	}
	c, _ := json.Marshal(r.Context)
	p, _ := json.Marshal(r.Payload)
	text := string(c) + " " + string(p)
	if len(text) > searchTextLimit {
		text = text[:searchTextLimit]
	}
	return strings.Contains(strings.ToLower(text), needle)
}

func problemEvent(r bridge.TimelineRecord) ProblemEvent {
	e := ProblemEvent{Seq: r.Sequence, Kind: r.Kind, Wall: r.WallTime, Trace: traceOf(r), Summary: eventSummary(r)}
	e.Tick, e.HasTick = tickOf(r)
	level, _ := r.Context["level"].(string)
	e.Problem = bridge.NativeReplyFailed(r) || r.Kind == "recording_gap" || level == "WARN" || level == "ERROR"
	var b strings.Builder
	if r.HasSeq {
		fmt.Fprintf(&b, "#%d ", r.Sequence)
	}
	b.WriteString(r.Kind)
	if e.HasTick {
		fmt.Fprintf(&b, " tick=%d", e.Tick)
	}
	if e.Trace != "" {
		b.WriteString(" trace=" + e.Trace)
	}
	if e.Summary != "" {
		b.WriteString("\n" + e.Summary)
	}
	if len(r.Payload) > 0 {
		if p, err := json.Marshal(r.Payload); err == nil {
			b.WriteString("\n" + string(p))
		}
	}
	e.Text = b.String()
	return e
}

// eventSummary is the one-line reading of a row, as the dashboard's feed
// gave it.
func eventSummary(r bridge.TimelineRecord) string {
	switch r.Kind {
	case "native_request", "native_call":
		s := toolOf(r.Payload)
		if e, ok := r.Payload["error"].(string); ok && e != "" {
			s += " - " + e
		}
		return s
	case "recording_gap":
		return fmt.Sprintf("%s %d..%d", orDefault(r.Reason, "gap"), r.Before, r.After)
	}
	if bridge.IsDecisionRow(r) {
		var words []string
		for _, key := range []string{"verdict", "reason", "target"} {
			if s, _ := r.Payload[key].(string); s != "" {
				words = append(words, s)
			}
		}
		return strings.Join(words, " ")
	}
	var parts []string
	if msg, ok := r.Payload["msg"].(string); ok && msg != "" {
		parts = append(parts, msg)
	}
	for _, key := range []string{"action", "goal", "plan", "reason", "cause", "outcome", "receipt", "stage", "elapsed_ms", "reads", "window_ticks", "err"} {
		v, ok := r.Payload[key]
		if !ok || v == nil || v == "" || v == false {
			continue
		}
		parts = append(parts, key+"="+scalarText(v))
	}
	return strings.Join(parts, " ")
}

func orDefault(s, d string) string {
	if s == "" {
		return d
	}
	return s
}

// toolOf is the inner rimgovernor/* method a row concerns: a reply names
// it as native_tool, a request only carries the games_call_tool wrapper
// and its arguments.
func toolOf(payload map[string]any) string {
	if n, ok := payload["native_tool"].(string); ok && n != "" {
		return n
	}
	tool, _ := payload["tool"].(string)
	if args, ok := payload["arguments"].(map[string]any); ok && tool == "games_call_tool" {
		if n, ok := args["tool"].(string); ok && n != "" {
			return n
		}
	}
	return tool
}

func scalarText(v any) string {
	switch x := v.(type) {
	case nil:
		return "null"
	case string:
		return x
	case float64:
		return strconv.FormatFloat(x, 'f', -1, 64)
	case bool:
		return fmt.Sprint(x)
	}
	b, err := json.Marshal(v)
	if err != nil {
		return "..."
	}
	return string(b)
}
