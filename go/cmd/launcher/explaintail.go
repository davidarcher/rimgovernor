package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The explanation reader: the concern_transitions table of the newest session
// store (state-*.sqlite under .rimgovernor/go) read in-process, read-only, and
// turned into one plain-language timeline per concern. There is no HTTP route;
// the Now tab (#2700) and the Ledger (#2701) read ExplainView from the app.

// gameTicksPerMinute is a game minute: domain.TicksPerHour ticks.
const gameTicksPerMinute = float64(domain.TicksPerHour) / 60

// explainReadTimeout bounds one read of the store.
const explainReadTimeout = 5 * time.Second

// TimelineEntry is one change of a concern's winning cause.
type TimelineEntry struct {
	Seq     uint64
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
	// (previous cause ""), in which case Previous is "".
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
	Available bool   // the store holds a concern_transitions row
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

// explainTail reads the explanation history of the newest session store in
// dir. Each view opens the store read-only and closes it again, so the
// launcher never holds the file the controller may move aside on a schema change.
type explainTail struct {
	mu  sync.Mutex
	dir string
}

func newExplainTail(dir string) *explainTail { return &explainTail{dir: dir} }

// newestState is the newest state-*.sqlite in dir ("" when none): the stamped
// names sort by start time, as StatePath relies on.
func newestState(dir string) string {
	matches, _ := filepath.Glob(filepath.Join(dir, "state-*.sqlite"))
	if len(matches) == 0 {
		return ""
	}
	sort.Strings(matches)
	return matches[len(matches)-1]
}

// view answers a poll. A missing store or an empty table is an unavailable
// view with the reason; an unreadable store is one too, never a crash.
func (t *explainTail) view() ExplainView {
	t.mu.Lock()
	defer t.mu.Unlock()
	path := newestState(t.dir)
	if path == "" {
		return ExplainView{Path: t.dir, Concerns: []ConcernTimeline{}, Empty: "No explanation history yet. It appears in the session store under " + t.dir + " once the controller has run."}
	}
	ctx, cancel := context.WithTimeout(context.Background(), explainReadTimeout)
	defer cancel()
	rows, err := store.ReadConcernTransitions(ctx, path)
	switch {
	case errors.Is(err, os.ErrNotExist):
		return ExplainView{Path: path, Concerns: []ConcernTimeline{}, Empty: "No explanation history yet. It appears at " + path + " once the controller has run."}
	case err != nil:
		return ExplainView{Path: path, Concerns: []ConcernTimeline{}, Empty: "The explanation history could not be read: " + err.Error()}
	}
	return buildExplainView(path, rows)
}

func buildExplainView(path string, rows []store.ConcernTransition) ExplainView {
	out := ExplainView{Path: path, Concerns: []ConcernTimeline{}}
	byConcern := map[string]*ConcernTimeline{}
	var order []string
	for _, r := range rows {
		tl := byConcern[r.Concern]
		if tl == nil {
			tl = &ConcernTimeline{Concern: r.Concern}
			byConcern[r.Concern] = tl
			order = append(order, r.Concern)
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

func timelineEntry(r store.ConcernTransition) TimelineEntry {
	e := TimelineEntry{Seq: uint64(r.Sequence), Wall: float64(r.WallMS) / 1000, Tick: r.Tick, HasTick: true,
		Outcome: r.Verdict, Cause: r.Cause, Subject: r.Subject, Method: r.Method}
	e.Text = causeText(e.Outcome, e.Cause, e.Subject)
	if r.PreviousKnown {
		if r.Previous == "" {
			e.PreviousClear = true
		} else {
			e.Previous = policy.Wording(policy.Cause(r.Previous), "")
		}
	}
	if r.Held != nil {
		e.HeldTicks, e.HasHeld = *r.Held, true
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
