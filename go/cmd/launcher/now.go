package main

import (
	"fmt"
	"math"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

// The Now tab's view-model builders (#1986): each turns one serve-client
// Reading into the strings the page prints, so the page holds no labels or
// ordering. A stale Reading renders its last good value with a notice; a
// 404 (Observe mode) renders one line and no value. The label maps are the
// dashboard's NowPanel and DevelopmentPanel ones.

// Feed says how current a panel is. Notice is empty for a fresh reading.
type Feed struct {
	HasValue  bool
	Stale     bool
	NotServed bool
	Notice    string
}

func feedOf[T any](r Reading[T], what string) Feed {
	f := Feed{HasValue: r.Value != nil, Stale: r.Stale, NotServed: r.NotServed}
	switch {
	case r.NotServed:
		f.Notice = what + " is not served: the controller is in Observe mode, which serves no colony readings."
	case r.Stale:
		f.Notice = "Stale: the last good reading, from " + r.At.Format("15:04:05") + ". " + r.Error
	case r.Value == nil && r.Error != "":
		f.Notice = "Unavailable: " + r.Error
	case r.Value == nil:
		f.Notice = "Waiting for the controller."
	}
	return f
}

// HeaderView is the strip over the Now tab: connection, tick, pause, colony.
type HeaderView struct {
	Feed
	Connection string
	Tick       string // "" when unknown
	Paused     string // "paused", "running" or "unknown"
	Colony     string
	Mode       string
}

func headerView(r Reading[httpapi.State]) HeaderView {
	v := HeaderView{Feed: feedOf(r, "The game state"), Paused: "unknown"}
	s := r.Value
	if s == nil {
		return v
	}
	v.Connection = "Not connected to the game"
	if s.Connected {
		v.Connection = "Connected"
	}
	if s.Status.Label != "" {
		v.Connection += " (" + s.Status.Label + ")"
	}
	if s.Game.Tick != nil {
		v.Tick = group(int64(*s.Game.Tick))
	}
	if s.Game.Paused != nil {
		v.Paused = map[bool]string{true: "paused", false: "running"}[*s.Game.Paused]
	}
	if s.Game.Stale {
		v.Paused += ", reading stale"
	}
	if s.Identity != nil && s.Identity.ColonyID != "" {
		v.Colony = fmt.Sprintf("colony %s, map %d", s.Identity.ColonyID, s.Identity.MapID)
	}
	v.Mode = s.Mode
	return v
}

// pacingLabels are the pacing reasons as the controller records them
// (spectator.PacingReason); the panel names the evidence, it does not advise.
var pacingLabels = map[spectator.PacingReason]string{
	spectator.ReasonUnknown:        "Pace unknown: no step has run yet",
	spectator.ReasonGovernorOff:    "Governor off: rounds are disabled",
	spectator.ReasonHeld:           "Held: a clock event awaits review",
	spectator.ReasonRefused:        "Window refused",
	spectator.ReasonRunning:        "Running",
	spectator.ReasonBudget:         "Between windows: the last one spent its tick budget",
	spectator.ReasonStopped:        "Stopped",
	spectator.ReasonCinematic:      "Cinematic: an interesting moment is slowed on purpose",
	spectator.ReasonAccelerated:    "Accelerated",
	spectator.ReasonFrameBudget:    "Frame-paced",
	spectator.ReasonForcedSlowdown: "Slowed by the game",
	spectator.ReasonRegulated:      "Regulated",
	spectator.ReasonBackoff:        "Backed off",
}

// stopReason drops the wire prefix: STOP_REASON_COLONIST_HEALTH reads
// "colonist health".
func stopReason(reason string) string {
	r := strings.ToLower(strings.ReplaceAll(strings.TrimPrefix(reason, "STOP_REASON_"), "_", " "))
	if r == "" {
		return "unspecified"
	}
	return r
}

// blockedLabel names a concern's blocker (policy.BlockedReason); a
// prerequisite names the concern that must land first.
func blockedLabel(blocked string) string {
	if rest, ok := strings.CutPrefix(blocked, "prerequisite:"); ok {
		return "Needs " + rest + " first"
	}
	switch blocked {
	case "":
		return "Progressing"
	case "no_worker":
		return "No capable worker available"
	case "native_ineligible":
		return "Native holds the order ineligible"
	case "reconcile_write":
		return "Reconciling an uncertain order"
	case "cooldown":
		return "Every method on cooldown"
	case "no_method":
		return "No method in play"
	}
	return blocked
}

// reasonLabels are the development deferral reasons as the controller
// records them.
var reasonLabels = map[string]string{
	"": "Eligible", "cancelled": "Cancelled", "emergency": "Emergency precedence", "startup_survival": "Startup survival precedence",
	"blocked": "Blocked", "existing_commitment": "Already committed", "labor_idle": "Committed work idle: slot released",
	"workers_unknown": "Worker count unknown", "no_workers": "No workers", "deficit_unknown": "Deficit unknown",
	"capacity_committed": "Waiting for capacity", "method_unavailable": "No method available", "labor_unavailable": "Waiting for labor",
	"risk_deferred": "Deferred: outdoor risk", "control_disabled": "Controller not in control", "stage_foothold": "Held at Foothold: shelter unmet",
	"workers_overcommitted": "Paused: open work holds every worker",
}

func reasonLabel(reason string) string {
	if l, ok := reasonLabels[reason]; ok {
		return l
	}
	return reason
}

// ConcernRow is one active concern's progress line.
type ConcernRow struct {
	Concern, Method, Expected, LastProgress, ReviewBy, Status string
	Blocked                                                   bool
}

// NowView is the stage, concerns, pacing and last stop.
type NowView struct {
	Feed
	Stage    string
	Pacing   string
	Concerns []ConcernRow
	LastStop string
}

func nowView(r Reading[spectator.Now]) NowView {
	v := NowView{Feed: feedOf(r, "The now panel"), Concerns: []ConcernRow{}}
	n := r.Value
	if n == nil {
		return v
	}
	if s := n.Stage; s == nil {
		v.Stage = "No round has derived a colony stage yet."
	} else {
		v.Stage = fmt.Sprintf("Stage %s since tick %s", s.Stage, group(int64(s.Since)))
		if s.Blocker != "" {
			v.Stage += fmt.Sprintf(" - next stage waits on %s: %s", s.Blocker, s.Reason)
		} else {
			v.Stage += " - every condition met"
		}
		if s.Held {
			v.Stage += " - development held"
		}
	}
	label, ok := pacingLabels[n.Pacing.Reason]
	if !ok {
		label = string(n.Pacing.Reason)
	}
	v.Pacing = label
	if n.Pacing.Detail != "" {
		v.Pacing += ": " + n.Pacing.Detail
	}
	v.Pacing += fmt.Sprintf(" - %s ticks/s", group(int64(math.Round(n.Pacing.EffectiveTPS))))
	if n.Pacing.PacedTPS > 0 {
		v.Pacing += fmt.Sprintf(" - holding %s ticks/s", group(int64(math.Round(n.Pacing.PacedTPS))))
	}
	if n.Pacing.WindowTicks > 0 {
		v.Pacing += fmt.Sprintf(" - last window %s ticks", group(n.Pacing.WindowTicks))
	}
	// The projection already orders the concerns, most urgent first.
	for _, c := range n.Concerns {
		row := ConcernRow{Concern: c.Concern, Method: c.Method, Expected: c.Expected, LastProgress: group(int64(c.LastProgress)),
			ReviewBy: "no deadline", Status: blockedLabel(c.Blocked), Blocked: c.Blocked != ""}
		if row.Method == "" {
			row.Method = "none"
		}
		if c.NextReview != 0 {
			row.ReviewBy = group(int64(c.NextReview))
		}
		if c.Observed != nil {
			row.Status += fmt.Sprintf(" - deficit %d%%", int(math.Round(*c.Observed*100)))
		}
		v.Concerns = append(v.Concerns, row)
	}
	v.LastStop = describeStop(n.LastStop, n.Stops)
	return v
}

func describeStop(s *spectator.Stop, c spectator.Counts) string {
	if s == nil {
		return "No window has stopped on this launch."
	}
	out := "Last stop: " + stopReason(s.Reason)
	if s.Evidence != "" {
		out += " (" + s.Evidence + ")"
	}
	out += " at tick " + group(s.Tick)
	if s.Benign {
		out += ", the controller's own"
	}
	if legs := stopLegs(s); len(legs) > 0 {
		out += " - " + strings.Join(legs, ", ")
	}
	return out + fmt.Sprintf(" - %d stop(s) this launch, %d on budget and %d reactive", c.Stops, c.Budget, c.Reactive)
}

// stopLegs is the latency split in reading order, skipping the legs the
// stop did not carry.
func stopLegs(s *spectator.Stop) []string {
	var legs []string
	if s.DetectTicks != nil {
		legs = append(legs, group(*s.DetectTicks)+" ticks to detect")
	}
	if s.StopTicks != nil {
		legs = append(legs, group(*s.StopTicks)+" ticks to stop")
	}
	if s.ObserveMs != nil {
		legs = append(legs, ms(*s.ObserveMs)+" unobserved in native")
	}
	if s.ActedMs != nil {
		legs = append(legs, ms(*s.ActedMs)+" to the step that acted")
	}
	if s.ReadmitMs != nil {
		legs = append(legs, ms(*s.ReadmitMs)+" paused before readmission")
	}
	return legs
}

// DevRow is one ranked development candidate.
type DevRow struct {
	Concern, Status, Score, Deficit, Risk, Waiting string
	Selected, InProgress                           bool
}

// DevView is the development priorities: the "why is nothing happening" list.
type DevView struct {
	Feed
	Summary  string
	Capacity string
	Rows     []DevRow
	Empty    string // why Rows is empty, when it is
}

func devView(r Reading[DevelopmentView]) DevView {
	v := DevView{Feed: feedOf(r, "Development priorities"), Rows: []DevRow{}}
	if r.Value == nil {
		return v
	}
	d := r.Value.Development
	if d == nil {
		v.Empty = "No round has ranked development yet."
		return v
	}
	workers := "unknown"
	if d.Workers != nil {
		workers = strconv.Itoa(*d.Workers)
	}
	committed := "none"
	if len(d.Committed) > 0 {
		committed = strings.Join(d.Committed, ", ")
	}
	v.Summary = fmt.Sprintf("Reviewed tick %s - automatic admission, at most %d - workers %s - committed %s", group(d.Tick), d.Capacity, workers, committed)
	limit := "No eligible concern waiting"
	if d.Limiting != "" {
		limit = "Limited by: " + reasonLabel(d.Limiting)
	}
	v.Capacity = fmt.Sprintf("Held by startup work: %d - %s", d.HeldWorkers, limit)
	blockers := map[string]string{}
	for _, p := range r.Value.Progress {
		if p.Blocked != "" {
			blockers[p.Concern] = p.Blocked
		}
	}
	for _, row := range d.Rows {
		out := DevRow{Concern: row.Concern, Selected: row.Selected, InProgress: row.Committed && !row.Selected,
			Score: strconv.FormatFloat(row.Score, 'f', 1, 64), Deficit: percent(row.Deficit), Risk: percent(row.Risk),
			Waiting: group(row.WaitingSince)}
		switch {
		case row.Selected:
			out.Status = "Selected"
		case row.Committed:
			out.Status = "In progress"
		default:
			out.Status = reasonLabel(row.Reason)
			if b := blockers[row.Concern]; row.Reason == "blocked" && b != "" {
				out.Status += ": " + blockedLabel(b)
			}
		}
		if row.Bottleneck != "" {
			out.Status += " (" + row.Bottleneck + ")"
		}
		v.Rows = append(v.Rows, out)
	}
	if len(v.Rows) == 0 {
		v.Empty = "No optional concerns are competing."
	}
	return v
}

func percent(v *float64) string {
	if v == nil {
		return "unknown"
	}
	return fmt.Sprintf("%d%%", int(math.Round(*v*100)))
}

func ms(v float64) string { return strconv.FormatFloat(math.Round(v*10)/10, 'f', -1, 64) + " ms" }

// group formats n with thousands separators.
func group(n int64) string {
	s := strconv.FormatInt(n, 10)
	neg := strings.HasPrefix(s, "-")
	s = strings.TrimPrefix(s, "-")
	for i := len(s) - 3; i > 0; i -= 3 {
		s = s[:i] + "," + s[i:]
	}
	if neg {
		s = "-" + s
	}
	return s
}
