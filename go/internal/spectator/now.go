// Package spectator projects the "now" panel: what the colony is
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

// PacingReason says why the clock runs as fast as it does right now.
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
	// The reasons a running player-accelerated window holds the
	// rate it does, from native's pacing reason on the step's status:
	// full acceleration, the frame budget keeping input and rendering
	// responsive, the game's own forced slowdown, the blind-tick
	// regulator, and the controller's backoff ceiling.
	ReasonAccelerated    PacingReason = "accelerated"
	ReasonFrameBudget    PacingReason = "frame_budget"
	ReasonForcedSlowdown PacingReason = "forced_slowdown"
	ReasonRegulated      PacingReason = "regulated"
	ReasonBackoff        PacingReason = "backoff"
)

// Now is the spectator panel.
type Now struct {
	// Tick is the game tick the controller last observed, when known.
	Tick *int64 `json:"tick"`
	// Stage is the colony stage with the first unmet condition of the next,
	// nil until a review filed one.
	Stage *Stage `json:"stage"`
	// Concerns are the active concerns' progress records, the most urgent first
	// (blocked before unblocked, then by review deadline), at most Concerns
	// rows.
	Concerns []Concern `json:"concerns"`
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

// Concern is one active concern's progress: the method it is working, the native
// observable that proves the method advances, when that last moved, when it
// is judged stalled and what blocks it now.
type Concern struct {
	Concern      string      `json:"concern"`
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
	// EffectiveTPS is the wall ticks per second the newest clock_step
	// recorded, paused time included; 0 until the launch has one.
	EffectiveTPS float64 `json:"effectiveTps"`
	// WindowTicks is the tick budget of the last window the scheduler sized,
	// 0 when it admitted none.
	WindowTicks int64 `json:"windowTicks"`
	// PacedTPS is the tick rate the running window held at the last step,
	// 0 when none ran.
	PacedTPS float64 `json:"pacedTps"`
}

// Stop is one clock stop and its latency split: the ticks between
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
// and the observed tick.
type Input struct {
	Stage          *policy.ColonyStageRecord
	Progress       []policy.ConcernProgress
	ReviewsEnabled bool
	// Holds are the clock holds awaiting review, by kind.
	Holds []string
	Tick  *int64
	// Concerns bounds the concern rows; 0 uses ConcernsShown.
	Concerns int
}

// ConcernsShown is the default concern-row bound: a panel, not a report.
const ConcernsShown = 6

// Project composes the panel from the timeline rows of one launch (oldest
// first, as the recorder wrote them) and the already-read state. It reads
// nothing else: no native call, no journal write, no speed request.
func Project(rows []bridge.TimelineRecord, in Input) Now {
	out := Now{Tick: in.Tick, Concerns: concerns(in.Progress, in.Concerns), Pacing: Pacing{Reason: ReasonUnknown}}
	if in.Stage != nil {
		out.Stage = &Stage{Stage: in.Stage.Stage.String(), Since: in.Stage.Since, Blocker: string(in.Stage.Blocker), Reason: in.Stage.Reason, Held: in.Stage.Held}
	}
	var refused, native string
	var admitted, haveStep bool
	var running bool
	for _, row := range rows {
		fields := bridge.RowFields(row)
		switch {
		case bridge.WindowRefusal(row):
			refused, admitted, haveStep = strings.Join(stringList(fields["refused"]), ", "), false, true
			if refused == "" {
				refused, _ = fields["reason"].(string)
			}
			if held := stringList(fields["held_by"]); len(held) > 0 {
				refused = strings.Join(append(stringList(fields["refused"]), "held by "+strings.Join(held, ", ")), ", ")
			}
		case bridge.IsWorkerStep(row):
			haveStep = true
			admitted, _ = fields["admitted"].(bool)
			running, _ = fields["running"].(bool)
			if admitted {
				refused = ""
			}
			if ticks, ok := number(fields["window_ticks"]); ok {
				out.Pacing.WindowTicks = int64(ticks)
			}
		case bridge.IsWindowStop(row):
			out.LastStop = stop(row)
			out.Stops.Stops++
			if out.LastStop.Reason == "STOP_REASON_TICK_BUDGET" {
				out.Stops.Budget++
			} else {
				out.Stops.Reactive++
			}
			running, admitted, haveStep = false, false, true
			native, out.Pacing.PacedTPS = "", 0
		case row.Kind == "clock_step":
			step := bridge.StepFields(row)
			// The step's clock status: native's pacing reason and rate
			// under a running window, and its effective speed.
			if tps, ok := number(step["effective_tps"]); ok && tps > 0 {
				out.Pacing.EffectiveTPS = tps
			}
			native, _ = step["pacing_reason"].(string)
			out.Pacing.PacedTPS, _ = number(step["paced_tps"])
			// The step that acted on the stop closes its wall legs: the
			// latency from the native stamp to this step, and the pause the
			// readmission ended.
			if out.LastStop == nil {
				continue
			}
			if stopped, _ := step["stop"].(bool); !stopped {
				continue
			}
			if acted, ok := number(step["stop_latency_ms"]); ok && acted >= 0 && out.LastStop.ActedMs == nil {
				out.LastStop.ActedMs = &acted
			}
			if paused, ok := number(step["stop_pause_s"]); ok && paused >= 0 && out.LastStop.ReadmitMs == nil {
				readmit := paused * 1000
				out.LastStop.ReadmitMs = &readmit
			}
		}
	}
	out.Pacing.Reason, out.Pacing.Detail = pacing(in, out, refused, admitted, running, haveStep)
	if out.Pacing.Reason == ReasonRunning {
		out.Pacing.Reason, out.Pacing.Detail = runningPace(native)
	}
	return out
}

// pacing picks the reason the panel shows, most binding first:
// the governor being off, a hold awaiting review, a refused
// window, a running window, then the last stop's own reason.
func pacing(in Input, out Now, refused string, admitted, running, haveStep bool) (PacingReason, string) {
	switch {
	case !in.ReviewsEnabled:
		return ReasonGovernorOff, "rounds are disabled"
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

// runningPace refines a running window's reason by native's pacing
// reason; a fixed-speed window, or none reported, stays running.
func runningPace(native string) (PacingReason, string) {
	switch native {
	case "accelerated":
		return ReasonAccelerated, "at the boosted rate"
	case "frame_budget":
		return ReasonFrameBudget, "ticks per frame held to the frame budget"
	case "forced_slowdown":
		return ReasonForcedSlowdown, "the game forced Normal speed"
	case "regulated":
		return ReasonRegulated, "blind-tick regulator"
	case "ceiling":
		return ReasonBackoff, "the controller lowered the rate to keep its evidence current"
	}
	return ReasonRunning, ""
}

// stop reads one clock_stop row, deriving the tick legs the row carries.
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

// concerns orders the progress records the way a watcher reads them: blocked
// concerns first, then the nearest review deadline, then the concern id so the
// order is stable; at most limit rows.
func concerns(records []policy.ConcernProgress, limit int) []Concern {
	if limit <= 0 {
		limit = ConcernsShown
	}
	out := make([]Concern, 0, len(records))
	for _, r := range records {
		out = append(out, Concern{Concern: string(r.Concern), Method: r.Method, Expected: r.Expected, LastProgress: r.LastProgress,
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
		return a.Concern < b.Concern
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
