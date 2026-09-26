// Package spectator projects the "now" panel (#632): what the colony is
// trying to do, what it last achieved, what holds it and why the governor
// paced or stopped the clock. Every field is a read of state the controller
// already keeps — the last review's records and the flight-recorder rows —
// so the panel is pure presentation: projecting it issues no native call,
// writes no journal row and requests no speed. A viewer that connects,
// watches and disconnects leaves the simulation contract untouched.
package spectator

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// PacingReason says why the clock runs as fast as it does right now. It is
// the one field a cinematic mode has to move: slowing an interesting moment
// is opt-in and shows up here as ReasonCinematic, never as a silent change
// of speed.
type PacingReason string

const (
	// ReasonUnknown: no step has run on this launch yet.
	ReasonUnknown PacingReason = "unknown"
	// ReasonGovernorOff: reviews are disabled, so the governor admits no
	// window; the game runs only if the player runs it.
	ReasonGovernorOff PacingReason = "governor_off"
	// ReasonHeld: a clock event awaits review, so no window may be admitted
	// until it is acknowledged.
	ReasonHeld PacingReason = "held"
	// ReasonRefused: the last step's arbitration refused the window it
	// sized, and Detail names the refusal reasons.
	ReasonRefused PacingReason = "window_refused"
	// ReasonRunning: a window is running at the pace the scheduler set.
	ReasonRunning PacingReason = "running"
	// ReasonBudget: the last window ended on its own tick budget — the
	// planning stop between windows, not an interruption.
	ReasonBudget PacingReason = "tick_budget"
	// ReasonStopped: something interrupted the window (a hazard, a letter,
	// a requested pause) and Detail names the stop reason.
	ReasonStopped PacingReason = "stopped"
	// ReasonCinematic: a cinematic mode is slowing an interesting moment on
	// purpose (#627 sets the mode; the panel only shows it).
	ReasonCinematic PacingReason = "cinematic"
)

// ModeAutonomous is the default pacing mode: the colony plays on without a
// viewer. A pacing_mode row naming another mode (cinematic) replaces it.
const ModeAutonomous = "autonomous"

// Now is the spectator panel.
type Now struct {
	// Tick is the game tick the controller last observed, when known.
	Tick *int64 `json:"tick"`
	// Stage is the colony stage with the first unmet condition of the next,
	// nil until a review filed one.
	Stage *Stage `json:"stage"`
	// Goals are the active goals' progress records, the most urgent first
	// (blocked before unblocked, then by review deadline), at most Goals
	// rows.
	Goals []Goal `json:"goals"`
	// Pacing is why the clock runs as it does and how fast it is actually
	// going.
	Pacing Pacing `json:"pacing"`
	// LastStop is the newest clock stop with its latency split, nil before
	// the first stop of the launch.
	LastStop *Stop `json:"lastStop"`
	// Stops counts this launch's stops by class.
	Stops Counts `json:"stops"`
}

// Stage is the colony stage as the last review left it.
type Stage struct {
	Stage   string      `json:"stage"`
	Since   domain.Tick `json:"since"`
	Blocker string      `json:"blocker"`
	Reason  string      `json:"reason"`
	Held    bool        `json:"held"`
}

// Goal is one active goal's progress: the method it is working, the native
// observable that proves the method advances, when that last moved, when it
// is judged stalled and what blocks it now.
type Goal struct {
	Goal         string      `json:"goal"`
	Method       string      `json:"method"`
	Expected     string      `json:"expected"`
	LastProgress domain.Tick `json:"lastProgress"`
	NextReview   domain.Tick `json:"nextReview"`
	Blocked      string      `json:"blocked"`
	Prerequisite string      `json:"prerequisite"`
	Observed     *float64    `json:"observed"`
}

// Pacing is the pacing reason with the speed it explains.
type Pacing struct {
	Reason PacingReason `json:"reason"`
	// Detail is the reason's evidence in the row's own words: the refusal
	// reasons, the stop reason, the holds.
	Detail string `json:"detail"`
	// Mode is the pacing mode in force (ModeAutonomous, or a cinematic mode
	// a pacing_mode row declared).
	Mode string `json:"mode"`
	// EffectiveTPS is the wall ticks per second the launch has actually
	// achieved, paused time included.
	EffectiveTPS float64 `json:"effectiveTps"`
	// WindowTicks is the tick budget of the last window the scheduler sized,
	// 0 when it admitted none.
	WindowTicks int64 `json:"windowTicks"`
}

// Stop is one clock stop and its latency split (#621): the ticks between
// the hazard arising, the supervisor raising the stop and the stop landing,
// then the wall legs — how long it sat unobserved in native (ObserveMs, on
// native's clock) and how long the controller took to readmit a window
// (ReadmitMs, on the controller's). Nothing subtracts one process's clock
// from another's.
type Stop struct {
	Reason   string `json:"reason"`
	Evidence string `json:"evidence"`
	Cursor   int64  `json:"cursor"`
	// Benign: the controller's own doing (a budget, a requested pause, a
	// latched watch, an informational letter) rather than an interruption.
	Benign         bool     `json:"benign"`
	Tick           int64    `json:"tick"`
	DetectedTick   *int64   `json:"detectedTick"`
	OccurrenceTick *int64   `json:"occurrenceTick"`
	DetectTicks    *int64   `json:"detectTicks"`
	StopTicks      *int64   `json:"stopTicks"`
	ObserveMs      *float64 `json:"observeMs"`
	ActedMs        *float64 `json:"actedMs"`
	ReadmitMs      *float64 `json:"readmitMs"`
}

// Counts classifies the launch's stops: those the window's own tick budget
// ended against every other kind.
type Counts struct {
	Stops    int `json:"stops"`
	Budget   int `json:"budget"`
	Reactive int `json:"reactive"`
}

// Input is the already-read state the projection composes: the last
// review's stage and progress records, whether reviews are enabled at all,
// the observed tick and the launch's wall TPS.
type Input struct {
	Stage          *policy.ColonyStageRecord
	Progress       []policy.GoalProgress
	ReviewsEnabled bool
	// Holds are the clock holds awaiting review, by kind.
	Holds []string
	Tick  *int64
	TPS   float64
	// Goals bounds the goal rows; 0 uses GoalsShown.
	Goals int
}

// GoalsShown is the default goal-row bound: a panel, not a report.
const GoalsShown = 6

// Project composes the panel from the timeline rows of one launch (oldest
// first, as the recorder wrote them) and the already-read state. It reads
// nothing else: no native call, no journal write, no speed request.
func Project(rows []bridge.TimelineRecord, in Input) Now {
	out := Now{Tick: in.Tick, Goals: goals(in.Progress, in.Goals), Pacing: Pacing{Reason: ReasonUnknown, Mode: ModeAutonomous, EffectiveTPS: in.TPS}}
	if in.Stage != nil {
		out.Stage = &Stage{Stage: in.Stage.Stage.String(), Since: in.Stage.Since, Blocker: string(in.Stage.Blocker), Reason: in.Stage.Reason, Held: in.Stage.Held}
	}
	var refused string
	var admitted, haveStep bool
	var running bool
	for _, row := range rows {
		switch row.Kind {
		case "pacing_mode":
			// #627's opt-in mode declares itself here; a cinematic mode is
			// visible as a pacing reason rather than an unexplained speed.
			if mode, ok := row.Payload["mode"].(string); ok && mode != "" {
				out.Pacing.Mode = mode
			}
		case "admission_refused":
			refused, admitted, haveStep = strings.Join(stringList(row.Payload["refused"]), ", "), false, true
			if held := stringList(row.Payload["held_by"]); len(held) > 0 {
				refused = strings.Join(append(stringList(row.Payload["refused"]), "held by "+strings.Join(held, ", ")), ", ")
			}
		case "scheduler_step":
			haveStep = true
			admitted, _ = row.Payload["admitted"].(bool)
			running, _ = row.Payload["running"].(bool)
			if admitted {
				refused = ""
			}
			if ticks, ok := number(row.Payload["window_ticks"]); ok {
				out.Pacing.WindowTicks = int64(ticks)
			}
		case "scheduler_stop":
			out.LastStop = stop(row)
			out.Stops.Stops++
			if out.LastStop.Reason == "STOP_REASON_TICK_BUDGET" {
				out.Stops.Budget++
			} else {
				out.Stops.Reactive++
			}
			running, admitted, haveStep = false, false, true
		case "clock_step":
			// The step that acted on the stop closes its wall legs: the
			// latency from the native stamp to this step, and the pause the
			// readmission ended.
			if out.LastStop == nil {
				continue
			}
			if stopped, _ := row.Payload["stop"].(bool); !stopped {
				continue
			}
			if acted, ok := number(row.Payload["stop_latency_ms"]); ok && acted >= 0 && out.LastStop.ActedMs == nil {
				out.LastStop.ActedMs = &acted
			}
			if paused, ok := number(row.Payload["stop_pause_s"]); ok && paused >= 0 && out.LastStop.ReadmitMs == nil {
				readmit := paused * 1000
				out.LastStop.ReadmitMs = &readmit
			}
		}
	}
	out.Pacing.Reason, out.Pacing.Detail = pacing(in, out, refused, admitted, running, haveStep)
	return out
}

// pacing picks the reason the panel shows, most binding first: a declared
// cinematic mode, the governor being off, a hold awaiting review, a refused
// window, a running window, then the last stop's own reason.
func pacing(in Input, out Now, refused string, admitted, running, haveStep bool) (PacingReason, string) {
	switch {
	case out.Pacing.Mode != ModeAutonomous:
		return ReasonCinematic, out.Pacing.Mode
	case !in.ReviewsEnabled:
		return ReasonGovernorOff, "routine reviews are disabled"
	case len(in.Holds) > 0:
		return ReasonHeld, strings.Join(in.Holds, ", ")
	case refused != "":
		return ReasonRefused, refused
	case admitted || running:
		return ReasonRunning, ""
	case out.LastStop != nil && out.LastStop.Reason == "STOP_REASON_TICK_BUDGET":
		return ReasonBudget, "the window spent its tick budget"
	case out.LastStop != nil:
		return ReasonStopped, out.LastStop.Reason
	case haveStep:
		return ReasonRunning, ""
	}
	return ReasonUnknown, ""
}

// stop reads one scheduler_stop row, deriving the tick legs the row carries.
func stop(row bridge.TimelineRecord) *Stop {
	out := &Stop{}
	out.Reason, _ = row.Payload["reason"].(string)
	out.Evidence, _ = row.Payload["evidence"].(string)
	out.Benign, _ = row.Payload["benign"].(bool)
	if cursor, ok := number(row.Payload["cursor"]); ok {
		out.Cursor = int64(cursor)
	}
	if tick, ok := number(row.Payload["tick"]); ok {
		out.Tick = int64(tick)
	}
	for key, field := range map[string]**int64{"detected_tick": &out.DetectedTick, "occurrence_tick": &out.OccurrenceTick, "detect_ticks": &out.DetectTicks, "stop_ticks": &out.StopTicks} {
		if value, ok := number(row.Payload[key]); ok {
			ticks := int64(value)
			*field = &ticks
		}
	}
	if age, ok := number(row.Payload["age_at_reply_ms"]); ok && age >= 0 {
		out.ObserveMs = &age
	}
	if out.Reason == "" {
		out.Reason = "STOP_REASON_UNSPECIFIED"
	}
	return out
}

// goals orders the progress records the way a watcher reads them: blocked
// goals first, then the nearest review deadline, then the goal id so the
// order is stable; at most limit rows.
func goals(records []policy.GoalProgress, limit int) []Goal {
	if limit <= 0 {
		limit = GoalsShown
	}
	out := make([]Goal, 0, len(records))
	for _, r := range records {
		out = append(out, Goal{Goal: string(r.Goal), Method: r.Method, Expected: r.Expected, LastProgress: r.LastProgress,
			NextReview: r.NextReview, Blocked: string(r.Blocked), Prerequisite: string(r.Blocked.Prerequisite()), Observed: r.Observed})
	}
	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if (a.Blocked != "") != (b.Blocked != "") {
			return a.Blocked != ""
		}
		if (a.NextReview == 0) != (b.NextReview == 0) {
			return b.NextReview == 0
		}
		if a.NextReview != b.NextReview {
			return a.NextReview < b.NextReview
		}
		return a.Goal < b.Goal
	})
	if len(out) > limit {
		out = out[:limit]
	}
	return out
}

// number reads a recorder payload number; the ring decodes every number as
// a float64, but an in-process row may carry the integer itself.
func number(value any) (float64, bool) {
	switch v := value.(type) {
	case float64:
		return v, true
	case int64:
		return float64(v), true
	case int:
		return float64(v), true
	case uint64:
		return float64(v), true
	}
	return 0, false
}

// stringList reads a recorder payload list of strings, tolerating the []any a
// decoded row carries.
func stringList(value any) []string {
	switch v := value.(type) {
	case []string:
		return v
	case []any:
		out := make([]string, 0, len(v))
		for _, item := range v {
			if text, ok := item.(string); ok && text != "" {
				out = append(out, text)
			}
		}
		return out
	}
	return nil
}
