package main

import (
	"fmt"
	"math"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

// The Now tab's view-model builders: each turns one serve-client
// Reading into the strings the page prints, so the page holds no labels or
// ordering. A stale Reading renders its last good value with a notice; a
// 404 (Observe mode) renders one line and no value. The label maps are the
// launcher's own. reportView joins the Now and
// routines readings into the four-section report.

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

// blockedLabel names a concern's blocker (policy.BlockedReason) in the
// wording table's sentence; a prerequisite names the concern that must land
// first.
func blockedLabel(blocked, subject string) string {
	if rest, ok := strings.CutPrefix(blocked, "prerequisite:"); ok {
		return "Needs " + concernLabel(rest) + " first"
	}
	if blocked == "" {
		return "Progressing"
	}
	return strings.TrimSuffix(policy.Wording(policy.Cause(blocked), subject), ".")
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
// feed. The page prints them as given. Feed is the Now feed's: with
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

func reportView(nr Reading[spectator.Now], dr Reading[RoundsView]) ReportView {
	v := ReportView{Feed: feedOf(nr, "The colony report"), Sections: []ReportSection{}}
	n := nr.Value
	if n == nil {
		return v
	}
	var reviewTick *int64
	if dr.Value != nil {
		reviewTick = dr.Value.Tick
	}
	devNote := feedOf(dr, "Round progress").Notice
	emergency := dr.Value != nil && len(dr.Value.Emergency) > 0
	idle := idleReasons[n.Pacing.Reason]

	// Headline.
	stage := "no colony stage derived yet"
	if n.Stage != nil {
		stage = "stage " + n.Stage.Stage
	}
	review := "last review unknown"
	if reviewTick != nil && n.Tick != nil {
		review = "last review " + span(*n.Tick-*reviewTick) + " ago"
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
		text += " - " + blockedLabel(c.Blocked, c.BlockedSubject)
		if idle {
			text = "Last on the list: " + text
		}
		doing.Lines = append(doing.Lines, ReportLine{Text: text})
		if !idle {
			doing.Lines = append(doing.Lines, ReportLine{Text: "Pace: " + pacingLine(n.Pacing)})
		}
	}
	pursuing := ReportSection{Title: "Pursuing", Note: devNote}
	if st := n.Stage; st != nil {
		text := fmt.Sprintf("Stage %s since tick %s", st.Stage, group(int64(st.Since)))
		if st.Blocker != "" {
			text += fmt.Sprintf(" - next stage waits on %s: %s", st.Blocker, st.Reason)
		}
		pursuing.Lines = append(pursuing.Lines, ReportLine{Text: text})
	} else {
		pursuing.Lines = append(pursuing.Lines, ReportLine{Text: "No round has derived a colony stage yet."})
	}
	waiting := ReportSection{Title: "Waiting", Note: devNote}
	for _, c := range n.Concerns {
		if c.Blocked != "" {
			waiting.Lines = append(waiting.Lines, ReportLine{Text: concernLabel(c.Concern) + " - " + blockedLabel(c.Blocked, c.BlockedSubject)})
		}
		if c.Method != "" {
			pursuing.Lines = append(pursuing.Lines, ReportLine{Text: concernLabel(c.Concern) + " - " + c.Method})
		}
	}
	if len(waiting.Lines) == 0 {
		waiting.Lines = append(waiting.Lines, ReportLine{Text: "No concern is waiting."})
	}

	// Concerns: the projection orders them, most urgent first.
	concerns := ReportSection{Title: "Concerns"}
	if emergency {
		text := "Emergency in force"
		concerns.Lines = append(concerns.Lines, ReportLine{Text: text + ". The feeds do not name the need; the controller log does.", Flag: "emergency"})
	}
	for _, c := range n.Concerns {
		method := c.Method
		if method == "" {
			method = "no method"
		}
		text := fmt.Sprintf("%s - %s - %s", concernLabel(c.Concern), method, blockedLabel(c.Blocked, c.BlockedSubject))
		if c.Observed != nil {
			text += fmt.Sprintf(" - deficit %d%%", int(math.Round(*c.Observed*100)))
		}
		text += " - " + deadline(c.NextReview, n.Tick)
		flag := ""
		switch {
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
