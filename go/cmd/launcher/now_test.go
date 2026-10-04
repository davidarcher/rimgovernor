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
	for in, want := range map[string]string{"": "Progressing", "no_worker": "No capable worker available", "prerequisite:Roof": "Needs Roof first", "odd": "odd"} {
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

func TestNowViewFresh(t *testing.T) {
	obs := 0.4
	n := spectator.Now{
		Stage:    &spectator.Stage{Stage: "Foothold", Since: 1200, Blocker: "shelter", Reason: "no roof", Held: true},
		Concerns: []spectator.Concern{{Concern: "Feed", Method: "", Expected: "stock", LastProgress: 5000, NextReview: 0, Blocked: "no_worker", Observed: &obs}, {Concern: "Roof", Method: "Build", NextReview: 9000}},
		Pacing:   spectator.Pacing{Reason: spectator.ReasonHeld, Detail: "raid", EffectiveTPS: 249.6, PacedTPS: 150, WindowTicks: 2500},
		LastStop: &spectator.Stop{Reason: "STOP_REASON_TICK_BUDGET", Tick: 8000, Benign: true, DetectTicks: ptr(int64(12)), ReadmitMs: ptr(1500.25)},
		Stops:    spectator.Counts{Stops: 3, Budget: 2, Reactive: 1},
	}
	v := nowView(Reading[spectator.Now]{Value: &n, At: time.Now()})
	if v.Notice != "" || !v.HasValue || v.Stale {
		t.Fatalf("%+v", v.Feed)
	}
	if !strings.Contains(v.Stage, "Foothold since tick 1,200") || !strings.Contains(v.Stage, "waits on shelter: no roof") || !strings.Contains(v.Stage, "development held") {
		t.Fatal(v.Stage)
	}
	if v.Pacing != "Held: a clock event awaits review: raid - 250 ticks/s - holding 150 ticks/s - last window 2,500 ticks" {
		t.Fatal(v.Pacing)
	}
	if len(v.Concerns) != 2 || v.Concerns[0].Concern != "Feed" || v.Concerns[0].Method != "none" || v.Concerns[0].ReviewBy != "no deadline" ||
		v.Concerns[0].Status != "No capable worker available - deficit 40%" || !v.Concerns[0].Blocked || v.Concerns[1].ReviewBy != "9,000" || v.Concerns[1].Status != "Progressing" {
		t.Fatalf("%+v", v.Concerns)
	}
	want := "Last stop: tick budget at tick 8,000, the controller's own - 12 ticks to detect, 1500.3 ms paused before readmission - 3 stop(s) this launch, 2 on budget and 1 reactive"
	if v.LastStop != want {
		t.Fatalf("%q", v.LastStop)
	}
}

func TestNowViewEmptyAndStates(t *testing.T) {
	n := spectator.Now{Pacing: spectator.Pacing{Reason: spectator.ReasonUnknown}}
	v := nowView(Reading[spectator.Now]{Value: &n})
	if !strings.HasPrefix(v.Stage, "No round has derived") || v.LastStop != "No window has stopped on this launch." || len(v.Concerns) != 0 {
		t.Fatalf("%+v", v)
	}
	stale := nowView(Reading[spectator.Now]{Value: &n, Stale: true, Error: "HTTP 500", At: time.Date(2026, 1, 1, 10, 30, 5, 0, time.Local)})
	if !stale.HasValue || !stale.Stale || stale.Stage == "" || stale.Notice != "Stale: the last good reading, from 10:30:05. HTTP 500" {
		t.Fatalf("%+v", stale)
	}
	ns := nowView(Reading[spectator.Now]{NotServed: true})
	if ns.HasValue || !ns.NotServed || !strings.Contains(ns.Notice, "Observe mode") || ns.Stage != "" {
		t.Fatalf("%+v", ns)
	}
	none := nowView(Reading[spectator.Now]{Error: "connection refused"})
	if none.HasValue || none.Stale || none.Notice != "Unavailable: connection refused" {
		t.Fatalf("%+v", none)
	}
	if w := nowView(Reading[spectator.Now]{}); w.Notice != "Waiting for the controller." {
		t.Fatal(w.Notice)
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

func TestDevViewOrderStatusAndBlockers(t *testing.T) {
	d := DevelopmentView{
		Development: &Development{Tick: 7000, Workers: ptr(3), Capacity: 2, Committed: []string{"Feed"}, HeldWorkers: 1, Limiting: "capacity_committed", Rows: []DevelopmentRow{
			{Concern: "Feed", Score: 3.14, Deficit: ptr(0.5), Selected: true},
			{Concern: "Wall", Score: 2, Committed: true, WaitingSince: 6000},
			{Concern: "Roof", Score: 1, Reason: "blocked", Bottleneck: "Construction"},
			{Concern: "Art", Score: 0.5, Reason: "labor_unavailable"},
		}},
		Progress: []ConcernBlock{{Concern: "Roof", Blocked: "cooldown"}, {Concern: "Feed"}},
	}
	v := devView(Reading[DevelopmentView]{Value: &d})
	if v.Summary != "Reviewed tick 7,000 - automatic admission, at most 2 - workers 3 - committed Feed" || v.Capacity != "Held by startup work: 1 - Limited by: Waiting for capacity" {
		t.Fatalf("%q / %q", v.Summary, v.Capacity)
	}
	got := []string{}
	for _, r := range v.Rows {
		got = append(got, r.Concern+"="+r.Status)
	}
	want := "Feed=Selected Wall=In progress Roof=Blocked: Every method on cooldown (Construction) Art=Waiting for labor"
	if strings.Join(got, " ") != want {
		t.Fatalf("%q", strings.Join(got, " "))
	}
	if v.Rows[0].Score != "3.1" || v.Rows[0].Deficit != "50%" || v.Rows[0].Risk != "unknown" || v.Rows[1].Waiting != "6,000" {
		t.Fatalf("%+v", v.Rows)
	}
	none := devView(Reading[DevelopmentView]{Value: &DevelopmentView{}})
	if none.Empty != "No round has ranked development yet." || len(none.Rows) != 0 {
		t.Fatalf("%+v", none)
	}
	ns := devView(Reading[DevelopmentView]{NotServed: true})
	if ns.HasValue || !strings.Contains(ns.Notice, "Observe mode") {
		t.Fatalf("%+v", ns)
	}
}
