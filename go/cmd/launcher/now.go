package main

import (
	"fmt"
	"math"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

// The Now tab's view-model builders (#1986): each turns one serve-client
// Reading into the strings the page prints, so the page holds no labels or
// ordering. A stale Reading renders its last good value with a notice; a
// 404 (Observe mode) renders one line and no value. The label maps are the
// dashboard's NowPanel and DevelopmentPanel ones. reportView joins the Now and
// routines readings into the four-section report (#2033).

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
		return "Needs " + concernLabel(rest) + " first"
	}
	if rest, ok := strings.CutPrefix(blocked, "planner:"); ok {
		return "Planner refused: " + rest
	}
	if rest, ok := strings.CutPrefix(blocked, "waiting:"); ok {
		return "Waiting: " + rest
	}
	switch blocked {
	case "":
		return "Progressing"
	case "held:emergency":
		return "Held for an emergency"
	case "held:stage":
		return "Held at the current stage"
	case "held:unavailable":
		return "Held: unavailable"
	case "held:opt-in":
		return "Held: not opted in"
	case "held:capacity":
		return "Held: waiting for capacity"
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

// ReportLine is one sentence of the report. Flag is "", "warn" or
// "emergency"; the page only styles by it.
type ReportLine struct {
	Text string
	Flag string
}

// ReportSection is one titled section (Doing, Pursuing, Concerns, Waiting).
// Note is the section's own feed notice (stale or unserved development
// priorities); Lines say what is known, or that nothing is.
type ReportSection struct {
	Title string
	Note  string
	Lines []ReportLine
}

// ReportView is the Now tab's report on the colony: a headline and the four
// sections in reading order, built from the spectator Now and the routines
// feed (#2033). The page prints them as given. Feed is the Now feed's: with
// no value the page shows its Notice alone (Observe mode serves neither).
type ReportView struct {
	Feed
	Headline string
	Sections []ReportSection
}

// ticksPerHour and ticksPerDay are RimWorld's clock.
const (
	ticksPerHour = 2500
	ticksPerDay  = 60000
)

// span reads a tick span in game time.
func span(ticks int64) string {
	one := func(v float64, unit string) string {
		return strconv.FormatFloat(math.Round(v*10)/10, 'f', -1, 64) + " " + unit
	}
	switch {
	case ticks < ticksPerHour:
		return group(max(ticks, 0)) + " ticks"
	case ticks < 2*ticksPerDay:
		return one(float64(ticks)/ticksPerHour, "game h")
	}
	return one(float64(ticks)/ticksPerDay, "game days")
}

// idleReasons are the pacing reasons in which no window is running.
var idleReasons = map[spectator.PacingReason]bool{
	spectator.ReasonUnknown: true, spectator.ReasonGovernorOff: true, spectator.ReasonHeld: true, spectator.ReasonRefused: true,
	spectator.ReasonBudget: true, spectator.ReasonStopped: true, spectator.ReasonBackoff: true,
}

func pacingLine(p spectator.Pacing) string {
	label, ok := pacingLabels[p.Reason]
	if !ok {
		label = string(p.Reason)
	}
	out := label
	if p.Detail != "" {
		out += ": " + p.Detail
	}
	out += fmt.Sprintf(" - %s ticks/s", group(int64(math.Round(p.EffectiveTPS))))
	if p.PacedTPS > 0 {
		out += fmt.Sprintf(" - holding %s ticks/s", group(int64(math.Round(p.PacedTPS))))
	}
	if p.WindowTicks > 0 {
		out += fmt.Sprintf(" - last window %s ticks", group(p.WindowTicks))
	}
	return out
}

func governorWords(r spectator.PacingReason) string {
	switch r {
	case spectator.ReasonGovernorOff:
		return "governor off"
	case spectator.ReasonHeld:
		return "governor held for a review"
	case spectator.ReasonUnknown:
		return "governor has not stepped yet"
	}
	return "governor running"
}

// movedAgo says how long ago a concern's native observable last moved.
func movedAgo(last domain.Tick, now *int64) string {
	switch {
	case last == 0:
		return "no movement recorded yet"
	case now == nil:
		return "last moved at tick " + group(int64(last))
	}
	return "last moved " + span(*now-int64(last)) + " ago"
}

// deadline says when a concern is judged stalled, relative to now when known.
func deadline(next domain.Tick, now *int64) string {
	switch {
	case next == 0:
		return "no review deadline"
	case now == nil:
		return "review by tick " + group(int64(next))
	case int64(next) <= *now:
		return "review overdue by " + span(*now-int64(next))
	}
	return "review in " + span(int64(next)-*now)
}

func reportView(nr Reading[spectator.Now], dr Reading[DevelopmentView]) ReportView {
	v := ReportView{Feed: feedOf(nr, "The colony report"), Sections: []ReportSection{}}
	n := nr.Value
	if n == nil {
		return v
	}
	var dev *Development
	var progress []ConcernBlock
	if dr.Value != nil {
		dev, progress = dr.Value.Development, dr.Value.Progress
	}
	devNote := feedOf(dr, "Development priorities").Notice
	blockers := map[string]string{}
	for _, p := range progress {
		if p.Blocked != "" {
			blockers[p.Concern] = p.Blocked
		}
	}
	emergency, deferred := false, 0
	if dev != nil {
		for _, row := range dev.Rows {
			if row.Reason == "emergency" {
				emergency = true
				deferred++
			}
		}
	}
	for _, c := range n.Concerns {
		emergency = emergency || c.Blocked == "held:emergency"
	}
	idle := idleReasons[n.Pacing.Reason]

	// Headline.
	stage := "no colony stage derived yet"
	if n.Stage != nil {
		stage = "stage " + n.Stage.Stage
	}
	review := "last review unknown"
	if dev != nil && n.Tick != nil {
		review = "last review " + span(*n.Tick-dev.Tick) + " ago"
	}
	head := []string{stage, governorWords(n.Pacing.Reason), review}
	if emergency {
		head = append(head, "an emergency is in force")
	}
	v.Headline = strings.ToUpper(head[0][:1]) + strings.Join(head, ", ")[1:] + "."

	// Doing: the most urgent active concern, or why nothing is.
	doing := ReportSection{Title: "Doing"}
	if idle {
		doing.Lines = append(doing.Lines, ReportLine{"Nothing is being worked. " + pacingLine(n.Pacing), "warn"})
	}
	switch {
	case len(n.Concerns) == 0:
		if !idle {
			doing.Lines = append(doing.Lines, ReportLine{Text: "No active concern has filed a progress record yet. " + pacingLine(n.Pacing)})
		}
	default:
		c := n.Concerns[0]
		text := concernLabel(c.Concern) + ": no method in play"
		if c.Method != "" {
			text = concernLabel(c.Concern) + ": " + c.Method
			if c.Expected != "" {
				text += ", to move " + c.Expected + " (" + movedAgo(c.LastProgress, n.Tick) + ")"
			}
		}
		text += " - " + blockedLabel(c.Blocked)
		if idle {
			text = "Last on the list: " + text
		}
		doing.Lines = append(doing.Lines, ReportLine{Text: text})
		if !idle {
			doing.Lines = append(doing.Lines, ReportLine{Text: "Pace: " + pacingLine(n.Pacing)})
		}
	}
	// Pursuing: the stage and its next condition, then the ranked
	// development rows holding or taking a slot.
	pursuing := ReportSection{Title: "Pursuing", Note: devNote}
	if s := n.Stage; s == nil {
		pursuing.Lines = append(pursuing.Lines, ReportLine{Text: "No round has derived a colony stage yet."})
	} else {
		text := fmt.Sprintf("Stage %s since tick %s", s.Stage, group(int64(s.Since)))
		if s.Blocker != "" {
			text += fmt.Sprintf(" - next stage waits on %s: %s", s.Blocker, s.Reason)
		} else {
			text += " - every next-stage condition is met"
		}
		if s.Held {
			text += " - development held"
		}
		pursuing.Lines = append(pursuing.Lines, ReportLine{Text: text})
	}
	waiting := ReportSection{Title: "Waiting", Note: devNote}
	switch {
	case dev == nil:
		none := ReportLine{Text: "No round has ranked development yet."}
		if devNote != "" {
			none = ReportLine{Text: "Development priorities are unavailable."}
		}
		pursuing.Lines = append(pursuing.Lines, none)
		waiting.Lines = append(waiting.Lines, none)
	default:
		workers := "unknown"
		if dev.Workers != nil {
			workers = strconv.Itoa(*dev.Workers)
		}
		committed := "none"
		if len(dev.Committed) > 0 {
			committed = strings.Join(dev.Committed, ", ")
		}
		limit := "no eligible concern is waiting"
		if dev.Limiting != "" {
			limit = "limited by " + strings.ToLower(reasonLabel(dev.Limiting))
		}
		waiting.Lines = append(waiting.Lines, ReportLine{Text: fmt.Sprintf("Capacity: at most %d automatic admission(s), workers %s, committed %s, %d held by startup work - %s", dev.Capacity, workers, committed, dev.HeldWorkers, limit)})
		pursued, waits := 0, 0
		for i, row := range dev.Rows {
			facts := fmt.Sprintf("score %s, deficit %s, risk %s", strconv.FormatFloat(row.Score, 'f', 1, 64), percent(row.Deficit), percent(row.Risk))
			switch {
			case row.Selected || row.Committed:
				status := "in progress"
				if row.Selected {
					status = "selected"
				}
				pursuing.Lines = append(pursuing.Lines, ReportLine{Text: fmt.Sprintf("%d. %s - %s - %s", i+1, concernLabel(row.Concern), status, facts)})
				pursued++
			default:
				why := reasonLabel(row.Reason)
				if b := blockers[row.Concern]; row.Reason == "blocked" && b != "" {
					why += ": " + blockedLabel(b)
				}
				if row.Bottleneck != "" {
					why += " (" + row.Bottleneck + ")"
				}
				text := fmt.Sprintf("%d. %s - %s - %s", i+1, concernLabel(row.Concern), why, facts)
				if row.WaitingSince > 0 && n.Tick != nil {
					text += ", waiting " + span(*n.Tick-row.WaitingSince)
				}
				flag := ""
				if row.Reason == "emergency" {
					flag = "emergency"
				}
				waiting.Lines = append(waiting.Lines, ReportLine{Text: text, Flag: flag})
				waits++
			}
		}
		if pursued == 0 {
			pursuing.Lines = append(pursuing.Lines, ReportLine{Text: "No development project is selected or in progress."})
		}
		if waits == 0 {
			waiting.Lines = append(waiting.Lines, ReportLine{Text: "No optional concern is waiting."})
		}
	}

	// Concerns: the projection orders them, most urgent first.
	concerns := ReportSection{Title: "Concerns"}
	if emergency {
		text := "Emergency in force"
		if deferred > 0 {
			text += fmt.Sprintf(": %d optional concern(s) deferred for emergency precedence", deferred)
		}
		concerns.Lines = append(concerns.Lines, ReportLine{Text: text + ". The feeds do not name the need; the controller log does.", Flag: "emergency"})
	}
	for _, c := range n.Concerns {
		method := c.Method
		if method == "" {
			method = "no method"
		}
		text := fmt.Sprintf("%s - %s - %s", concernLabel(c.Concern), method, blockedLabel(c.Blocked))
		if c.Observed != nil {
			text += fmt.Sprintf(" - deficit %d%%", int(math.Round(*c.Observed*100)))
		}
		text += " - " + deadline(c.NextReview, n.Tick)
		flag := ""
		switch {
		case c.Blocked == "held:emergency":
			flag = "emergency"
		case c.Blocked != "":
			flag = "warn"
		}
		concerns.Lines = append(concerns.Lines, ReportLine{Text: text, Flag: flag})
	}
	if len(n.Concerns) == 0 {
		concerns.Lines = append(concerns.Lines, ReportLine{Text: "No active concern has filed a progress record yet."})
	}
	concerns.Lines = append(concerns.Lines, ReportLine{Text: describeStop(n.LastStop, n.Stops)})

	v.Sections = []ReportSection{doing, pursuing, concerns, waiting}
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
