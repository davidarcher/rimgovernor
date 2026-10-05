package main

import (
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

func ptr[T any](v T) *T { return &v }

func TestLabels(t *testing.T) {
	if got := stopReason("STOP_REASON_COLONIST_HEALTH"); got != "colonist health" {
		t.Fatal(got)
	}
	if got := stopReason(""); got != "unspecified" {
		t.Fatal(got)
	}
	for in, want := range map[string]string{"": "Progressing", "no_worker": "No capable worker available", "prerequisite:Roof": "Needs Roof first", "odd": "odd",
		"held:emergency": "Held for an emergency", "planner:no site": "Planner refused: no site", "waiting:wood": "Waiting: wood"} {
		if got := blockedLabel(in); got != want {
			t.Fatalf("%q -> %q", in, got)
		}
	}
	if reasonLabel("capacity_committed") != "Waiting for capacity" || reasonLabel("new_reason") != "new_reason" {
		t.Fatal("reasonLabel")
	}
	if group(1234567) != "1,234,567" || group(12) != "12" || group(-4200) != "-4,200" {
		t.Fatal("group")
	}
}

func TestSpan(t *testing.T) {
	for in, want := range map[int64]string{-5: "0 ticks", 900: "900 ticks", 5000: "2 game h", 6250: "2.5 game h", 150000: "2.5 game days"} {
		if got := span(in); got != want {
			t.Fatalf("%d -> %q", in, got)
		}
	}
}

func TestHeaderView(t *testing.T) {
	st := httpapi.State{Connected: true, Mode: "automate", Status: httpapi.Status{Label: "ok"}, Identity: &httpapi.Identity{ColonyID: "c1", MapID: 2}}
	st.Game.Paused = ptr(true)
	st.Game.Tick = ptr(domain.Tick(4200))
	v := headerView(Reading[httpapi.State]{Value: &st})
	if v.Connection != "Connected (ok)" || v.Tick != "4,200" || v.Paused != "paused" || v.Colony != "colony c1, map 2" || v.Mode != "automate" {
		t.Fatalf("%+v", v)
	}
	if u := headerView(Reading[httpapi.State]{Value: &httpapi.State{}}); u.Paused != "unknown" || u.Tick != "" || u.Connection != "Not connected to the game" {
		t.Fatalf("%+v", u)
	}
}

// sectionText flattens one section to "flag|text" lines.
func sectionText(v ReportView, title string) (ReportSection, string) {
	for _, s := range v.Sections {
		if s.Title == title {
			var b strings.Builder
			for _, l := range s.Lines {
				b.WriteString(l.Flag + "|" + l.Text + "\n")
			}
			return s, b.String()
		}
	}
	return ReportSection{}, ""
}

func TestReportView(t *testing.T) {
	healthy := spectator.Now{
		Tick:     ptr(int64(10000)),
		Stage:    &spectator.Stage{Stage: "Foothold", Since: 1200, Blocker: "shelter", Reason: "roof 40% of 80%"},
		Concerns: []spectator.Concern{{Concern: "Roof", Method: "Build", Expected: "roofed cells", LastProgress: 5000, NextReview: 12500}, {Concern: "Feed", Blocked: "no_worker", Observed: ptr(0.4)}},
		Pacing:   spectator.Pacing{Reason: spectator.ReasonRunning, EffectiveTPS: 249.6},
		LastStop: &spectator.Stop{Reason: "STOP_REASON_TICK_BUDGET", Tick: 8000, Benign: true, DetectTicks: ptr(int64(12)), ReadmitMs: ptr(1500.25)},
		Stops:    spectator.Counts{Stops: 3, Budget: 2, Reactive: 1},
	}
	dev := DevelopmentView{
		Development: &Development{Tick: 7500, Workers: ptr(3), Capacity: 1, Committed: []string{"Roof"}, HeldWorkers: 1, Limiting: "capacity_committed", Rows: []DevelopmentRow{
			{Concern: "Roof", Score: 3.14, Deficit: ptr(0.5), Committed: true},
			{Concern: "Art", Score: 1, Reason: "blocked", Bottleneck: "Construction", WaitingSince: 6000},
			{Concern: "Wall", Score: 0.5, Reason: "labor_unavailable"},
		}},
		Progress: []ConcernBlock{{Concern: "Art", Blocked: "cooldown"}},
	}
	paced := func(r spectator.PacingReason, detail string) spectator.Now {
		n := healthy
		n.Pacing = spectator.Pacing{Reason: r, Detail: detail}
		return n
	}
	held, off := paced(spectator.ReasonHeld, "raid"), paced(spectator.ReasonGovernorOff, "rounds are disabled")
	stopped := paced(spectator.ReasonStopped, "tick budget")
	stopped.Concerns = nil
	emergencyNow := healthy
	emergencyNow.Concerns = []spectator.Concern{{Concern: "Fire", Method: "Extinguish", Expected: "burning cells", LastProgress: 9900, NextReview: 9000, Blocked: "no_worker"}, {Concern: "Roof", Blocked: "held:emergency"}}
	emergencyDev := DevelopmentView{Development: &Development{Tick: 9000, Rows: []DevelopmentRow{{Concern: "Art", Reason: "emergency"}}}}
	at := time.Date(2026, 1, 1, 10, 30, 5, 0, time.Local)
	const lastGood = "Stale: the last good reading, from 10:30:05. HTTP 500"

	cases := []struct {
		name     string
		now      Reading[spectator.Now]
		dev      Reading[DevelopmentView]
		headline string
		contains map[string][]string // section -> "flag|text" substrings
		notice   string
		hasValue bool
		stale    bool
	}{
		{name: "healthy with an active concern", now: Reading[spectator.Now]{Value: &healthy}, dev: Reading[DevelopmentView]{Value: &dev}, hasValue: true,
			headline: "Stage Foothold, governor running, last review 1 game h ago.",
			contains: map[string][]string{
				"Doing":    {"|Roof: Build, to move roofed cells (last moved 2 game h ago) - Progressing", "|Pace: Running - 250 ticks/s"},
				"Pursuing": {"|Stage Foothold since tick 1,200 - next stage waits on shelter: roof 40% of 80%", "|1. Roof - in progress - score 3.1, deficit 50%, risk unknown"},
				"Concerns": {"|Roof - Build - Progressing - review in 1 game h", "warn|Feed - no method - No capable worker available - deficit 40% - no review deadline",
					"|Last stop: tick budget at tick 8,000, the controller's own - 12 ticks to detect, 1500.3 ms paused before readmission - 3 stop(s) this launch, 2 on budget and 1 reactive"},
				"Waiting": {"|Capacity: at most 1 automatic admission(s), workers 3, committed Roof, 1 held by startup work - limited by waiting for capacity",
					"|2. Art - Blocked: Every method on cooldown (Construction) - score 1.0, deficit unknown, risk unknown, waiting 1.6 game h", "|3. Wall - Waiting for labor"},
			}},
		{name: "idle: held", now: Reading[spectator.Now]{Value: &held}, dev: Reading[DevelopmentView]{Value: &dev}, hasValue: true,
			headline: "Stage Foothold, governor held for a review, last review 1 game h ago.",
			contains: map[string][]string{"Doing": {"warn|Nothing is being worked. Held: a clock event awaits review: raid", "|Last on the list: Roof: Build"}}},
		{name: "idle: governor off", now: Reading[spectator.Now]{Value: &off}, dev: Reading[DevelopmentView]{Value: &dev}, hasValue: true,
			headline: "Stage Foothold, governor off, last review 1 game h ago.",
			contains: map[string][]string{"Doing": {"warn|Nothing is being worked. Governor off: rounds are disabled"}}},
		{name: "idle: stopped, no concerns, nothing ranked", now: Reading[spectator.Now]{Value: &stopped}, dev: Reading[DevelopmentView]{Value: &DevelopmentView{}}, hasValue: true,
			headline: "Stage Foothold, governor running, last review unknown.",
			contains: map[string][]string{
				"Doing":    {"warn|Nothing is being worked. Stopped: tick budget"},
				"Concerns": {"|No active concern has filed a progress record yet."},
				"Pursuing": {"|No round has ranked development yet."},
				"Waiting":  {"|No round has ranked development yet."},
			}},
		{name: "emergency", now: Reading[spectator.Now]{Value: &emergencyNow}, dev: Reading[DevelopmentView]{Value: &emergencyDev}, hasValue: true,
			headline: "Stage Foothold, governor running, last review 1,000 ticks ago, an emergency is in force.",
			contains: map[string][]string{
				"Concerns": {"emergency|Emergency in force: 1 optional concern(s) deferred for emergency precedence.", "warn|Fire - Extinguish - No capable worker available - review overdue by 1,000 ticks", "emergency|Roof - no method - Held for an emergency"},
				"Waiting":  {"emergency|1. Art - Emergency precedence"},
				"Doing":    {"|Fire: Extinguish, to move burning cells (last moved 100 ticks ago)"},
			}},
		{name: "stale development keeps its last value", now: Reading[spectator.Now]{Value: &healthy}, hasValue: true,
			dev:      Reading[DevelopmentView]{Value: &dev, Stale: true, Error: "HTTP 500", At: at},
			headline: "Stage Foothold, governor running, last review 1 game h ago.",
			contains: map[string][]string{"Waiting": {"|3. Wall - Waiting for labor"}}},
		{name: "stale now keeps its last value", now: Reading[spectator.Now]{Value: &healthy, Stale: true, Error: "HTTP 500", At: at},
			dev: Reading[DevelopmentView]{Value: &dev}, hasValue: true, stale: true, notice: lastGood,
			headline: "Stage Foothold, governor running, last review 1 game h ago."},
		{name: "observe mode", now: Reading[spectator.Now]{NotServed: true}, dev: Reading[DevelopmentView]{NotServed: true},
			notice: "The colony report is not served: the controller is in Observe mode, which serves no colony readings."},
		{name: "never read", now: Reading[spectator.Now]{Error: "connection refused"}, notice: "Unavailable: connection refused"},
		{name: "waiting", now: Reading[spectator.Now]{}, notice: "Waiting for the controller."},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			v := reportView(c.now, c.dev)
			if v.HasValue != c.hasValue || v.Stale != c.stale || v.Notice != c.notice {
				t.Fatalf("feed %+v", v.Feed)
			}
			if !c.hasValue {
				if v.Headline != "" || len(v.Sections) != 0 {
					t.Fatalf("a failed reading shows no report: %+v", v)
				}
				return
			}
			if v.Headline != c.headline {
				t.Fatalf("headline %q", v.Headline)
			}
			if len(v.Sections) != 4 || v.Sections[0].Title != "Doing" || v.Sections[1].Title != "Pursuing" || v.Sections[2].Title != "Concerns" || v.Sections[3].Title != "Waiting" {
				t.Fatalf("sections %+v", v.Sections)
			}
			for title, want := range c.contains {
				_, got := sectionText(v, title)
				for _, w := range want {
					if !strings.Contains(got, w) {
						t.Errorf("%s lacks %q in:\n%s", title, w, got)
					}
				}
			}
		})
	}

	// A development reading that failed outright says so in the sections that
	// need it instead of printing an empty list.
	v := reportView(Reading[spectator.Now]{Value: &healthy}, Reading[DevelopmentView]{Error: "connection refused"})
	for _, title := range []string{"Pursuing", "Waiting"} {
		s, got := sectionText(v, title)
		if s.Note != "Unavailable: connection refused" || !strings.Contains(got, "Development priorities are unavailable.") {
			t.Fatalf("%s: %+v %s", title, s, got)
		}
	}
	// A stale development reading carries its notice on the sections.
	v = reportView(Reading[spectator.Now]{Value: &healthy}, Reading[DevelopmentView]{Value: &dev, Stale: true, Error: "HTTP 500", At: at})
	if s, _ := sectionText(v, "Waiting"); s.Note != lastGood {
		t.Fatalf("%q", s.Note)
	}
}
