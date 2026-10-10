package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The explanation tail: explain.jsonl under the profile read in-process, like
// the Problems and Log tabs, and turned into one plain-language timeline per
// concern. There is no HTTP route; the Now tab (#2700) and the Ledger (#2701)
// read ExplainView from the app.

// explainFile is the explanation ring beside flight.jsonl.
const explainFile = "explain.jsonl"

const concernTransitionKind = "concern_transition"

// gameTicksPerMinute is a game minute: domain.TicksPerHour ticks.
const gameTicksPerMinute = float64(domain.TicksPerHour) / 60

// TimelineEntry is one change of a concern's winning cause.
type TimelineEntry struct {
	Seq     uint64
	Run     string
	Wall    float64 // unix seconds
	Tick    int64
	HasTick bool
	Outcome string // the planner verdict's outcome word: refused, waiting, admitted, ...
	Cause   string // policy.Cause wire value; "" when the concern cleared
	Subject string // bounded subject; "" when none
	// Method is the concern's method when filed, "" when none. On an admit row
	// it may be the previous review's method.
	Method string
	// Text is the plain-language sentence for this state: policy.Wording for a
	// cause, a fixed sentence for an outcome that carries none.
	Text string
	// Previous is the sentence for the state this row replaced, "" when
	// unknown. PreviousClear is true when the replaced state was a clear
	// (previous_reason ""), in which case Previous is "".
	Previous      string
	PreviousClear bool
	// Held is how long that previous state stood in game time ("held for 12
	// min"), "" when unknown (first row after a restart, or a clock that
	// went backwards).
	Held      string
	HeldTicks int64
	HasHeld   bool
}

// ConcernTimeline is one concern's entries, oldest first.
type ConcernTimeline struct {
	Concern string // the row's target, kept as written
	Entries []TimelineEntry
	// Current is the newest entry's sentence.
	Current string
}

// ExplainView is what the Now tab and Ledger read.
type ExplainView struct {
	Available bool   // the ring holds a concern_transition row
	Empty     string // why not, when not Available
	Path      string
	// Concerns is ordered by newest entry first.
	Concerns []ConcernTimeline
}

// Timeline is concern's timeline, or nil when it has none.
func (v ExplainView) Timeline(concern string) *ConcernTimeline {
	for i := range v.Concerns {
		if v.Concerns[i].Concern == concern {
			return &v.Concerns[i]
		}
	}
	return nil
}

// explainTail follows the explanation ring, decoding only what was appended
// since the last call, and recomputes the view only when the ring grew.
type explainTail struct {
	mu     sync.Mutex
	path   string
	reader *bridge.TimelineReader
	key    string
	cached ExplainView
}

func newExplainTail(flightPath string) *explainTail {
	path := filepath.Join(filepath.Dir(flightPath), explainFile)
	return &explainTail{path: path, reader: bridge.NewTailTimelineReader(path, launcherRingFiles)}
}

// view answers a poll. A missing or empty ring is an unavailable view with the
// reason; a corrupt line or a gap is skipped, never an error.
func (t *explainTail) view() ExplainView {
	t.mu.Lock()
	defer t.mu.Unlock()
	rows, err := t.reader.Read()
	if err != nil {
		t.key = ""
		return ExplainView{Path: t.path, Concerns: []ConcernTimeline{}, Empty: "The explanation history could not be read: " + err.Error()}
	}
	key := ""
	if n := len(rows); n > 0 {
		last := rows[n-1]
		key = fmt.Sprintf("%d/%d/%g", n, last.Sequence, last.WallTime)
	}
	if key != "" && key == t.key {
		return t.cached
	}
	out := buildExplainView(t.path, rows)
	if len(rows) == 0 {
		if _, statErr := os.Stat(t.path); statErr != nil {
			out.Empty = "No explanation history yet. It appears at " + t.path + " once the controller has run."
		}
	}
	t.key, t.cached = key, out
	return out
}

func buildExplainView(path string, rows []bridge.TimelineRecord) ExplainView {
	out := ExplainView{Path: path, Concerns: []ConcernTimeline{}}
	byConcern := map[string]*ConcernTimeline{}
	var order []string
	for _, r := range rows {
		if r.Kind != concernTransitionKind {
			continue
		}
		concern, _ := r.Payload["target"].(string)
		if concern == "" {
			continue
		}
		tl := byConcern[concern]
		if tl == nil {
			tl = &ConcernTimeline{Concern: concern}
			byConcern[concern] = tl
			order = append(order, concern)
		}
		tl.Entries = append(tl.Entries, timelineEntry(r))
	}
	if len(order) == 0 {
		out.Empty = "The explanation history is empty."
		return out
	}
	out.Available = true
	for _, c := range order {
		tl := byConcern[c]
		tl.Current = tl.Entries[len(tl.Entries)-1].Text
		out.Concerns = append(out.Concerns, *tl)
	}
	sort.SliceStable(out.Concerns, func(i, j int) bool {
		a, b := out.Concerns[i].Entries, out.Concerns[j].Entries
		return a[len(a)-1].Wall > b[len(b)-1].Wall
	})
	return out
}

func timelineEntry(r bridge.TimelineRecord) TimelineEntry {
	attrs, _ := r.Payload["attrs"].(map[string]any)
	e := TimelineEntry{Seq: r.Sequence, Run: r.Run, Wall: r.WallTime}
	e.Tick, e.HasTick = tickOf(r)
	e.Outcome, _ = r.Payload["verdict"].(string)
	e.Cause, _ = r.Payload["reason"].(string)
	e.Subject, _ = attrs["subject"].(string)
	e.Method, _ = attrs["method"].(string)
	e.Text = causeText(e.Outcome, e.Cause, e.Subject)
	if prev, ok := attrs["previous_reason"].(string); ok {
		if prev == "" {
			e.PreviousClear = true
		} else {
			e.Previous = policy.Wording(policy.Cause(prev), "")
		}
	}
	if v, ok := attrs["held_ticks"].(float64); ok && v >= 0 {
		e.HeldTicks, e.HasHeld = int64(v), true
		e.Held = "held for " + gameDuration(e.HeldTicks)
	}
	return e
}

// causeText is the sentence for a state: the wording table's for a cause, a
// fixed one for a clear whose outcome says why.
func causeText(outcome, cause, subject string) string {
	if cause != "" {
		return policy.Wording(policy.Cause(cause), subject)
	}
	switch outcome {
	case "admitted":
		return "The bot started on it."
	case "nothing_to_do":
		return "There is nothing left to do."
	case "disabled":
		return "This is turned off."
	}
	return "Nothing is holding it up."
}

// gameDuration renders game ticks as game time: "under a minute", "12 min",
// "3 h", "2 days".
func gameDuration(ticks int64) string {
	minutes := int64(float64(ticks)/gameTicksPerMinute + 0.5)
	switch {
	case minutes < 1:
		return "under a minute"
	case minutes < 60:
		return fmt.Sprintf("%d min", minutes)
	case minutes < 60*24:
		return fmt.Sprintf("%d h", (minutes+30)/60)
	}
	days := (minutes + 12*60) / (60 * 24)
	if days == 1 {
		return "1 day"
	}
	return fmt.Sprintf("%d days", days)
}
