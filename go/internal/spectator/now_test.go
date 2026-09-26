package spectator

import (
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func row(kind string, sequence uint64, payload map[string]any) bridge.TimelineRecord {
	return bridge.TimelineRecord{Kind: kind, Sequence: sequence, HasSeq: true, Run: "run", WallTime: float64(sequence), Payload: payload}
}

// stopRow is a scheduler_stop row as clockPollEvents writes it, decoded
// from the ring (every number a float64).
func stopRow(sequence uint64, reason string, extra map[string]any) bridge.TimelineRecord {
	payload := map[string]any{"reason": reason, "evidence": "health", "cursor": float64(41), "benign": false, "tick": float64(5040)}
	for key, value := range extra {
		payload[key] = value
	}
	return row("scheduler_stop", sequence, payload)
}

func TestProjectStageGoalsAndStop(t *testing.T) {
	stage := &policy.ColonyStageRecord{Stage: policy.StageReserves, Since: 4000, Blocker: policy.StageBlockerWood, Reason: "wood floor 120 of 400"}
	observed := 0.25
	progress := []policy.GoalProgress{
		{Goal: "MaintainResource-WoodLog", Method: "chop", Expected: "wood in storage", LastProgress: 4900, NextReview: 7000, Observed: &observed},
		{Goal: "MaintainFoodStorage", Method: "hunt", Expected: "designated animal killed", LastProgress: 4500, NextReview: 6000, Blocked: policy.BlockedNoWorker},
		{Goal: "EnsureShelter", Method: "build", Expected: "construction observed", LastProgress: 4990},
	}
	tick := int64(5050)
	rows := []bridge.TimelineRecord{
		row("scheduler_step", 1, map[string]any{"admitted": true, "running": true, "window_ticks": float64(2500)}),
		stopRow(2, "STOP_REASON_COLONIST_HEALTH", map[string]any{"detected_tick": float64(5030), "stop_ticks": float64(10), "occurrence_tick": float64(5000), "detect_ticks": float64(30), "age_at_reply_ms": float64(12)}),
		row("clock_step", 3, map[string]any{"stop": true, "stop_latency_ms": float64(40), "stop_pause_s": float64(0.5)}),
	}
	now := Project(rows, Input{Stage: stage, Progress: progress, ReviewsEnabled: true, Tick: &tick, TPS: 820})

	if now.Tick == nil || *now.Tick != 5050 {
		t.Fatal(now.Tick)
	}
	if now.Stage == nil || now.Stage.Stage != "Reserves" || now.Stage.Since != 4000 || now.Stage.Blocker != "wood" || now.Stage.Reason == "" || now.Stage.Held {
		t.Fatal(now.Stage)
	}
	// Blocked first, then the nearest review deadline, then no deadline.
	want := []string{"MaintainFoodStorage", "MaintainResource-WoodLog", "EnsureShelter"}
	if len(now.Goals) != 3 {
		t.Fatal(now.Goals)
	}
	for i, goal := range want {
		if now.Goals[i].Goal != goal {
			t.Fatal(i, now.Goals[i].Goal, goal)
		}
	}
	if now.Goals[0].Blocked != "no_worker" || now.Goals[0].Method != "hunt" || now.Goals[0].Expected == "" {
		t.Fatal(now.Goals[0])
	}
	if now.Goals[1].Observed == nil || *now.Goals[1].Observed != 0.25 {
		t.Fatal(now.Goals[1])
	}
	stop := now.LastStop
	if stop == nil || stop.Reason != "STOP_REASON_COLONIST_HEALTH" || stop.Evidence != "health" || stop.Cursor != 41 || stop.Benign || stop.Tick != 5040 {
		t.Fatal(stop)
	}
	if *stop.DetectedTick != 5030 || *stop.OccurrenceTick != 5000 || *stop.DetectTicks != 30 || *stop.StopTicks != 10 {
		t.Fatal(stop)
	}
	if *stop.ObserveMs != 12 || *stop.ActedMs != 40 || *stop.ReadmitMs != 500 {
		t.Fatal(stop)
	}
	if now.Stops != (Counts{Stops: 1, Reactive: 1}) {
		t.Fatal(now.Stops)
	}
	// A reactive stop with nothing admitted since is the pacing reason.
	if now.Pacing.Reason != ReasonStopped || now.Pacing.Detail != "STOP_REASON_COLONIST_HEALTH" || now.Pacing.Mode != ModeAutonomous {
		t.Fatal(now.Pacing)
	}
	if now.Pacing.EffectiveTPS != 820 || now.Pacing.WindowTicks != 2500 {
		t.Fatal(now.Pacing)
	}
}

func TestProjectPacingReasons(t *testing.T) {
	running := row("scheduler_step", 1, map[string]any{"admitted": true, "running": true, "window_ticks": float64(1000)})
	refused := row("admission_refused", 2, map[string]any{"refused": []any{"stale_facts", "combat_plan"}, "held_by": []any{"draft"}})
	budget := stopRow(3, "STOP_REASON_TICK_BUDGET", nil)
	for _, c := range []struct {
		name   string
		rows   []bridge.TimelineRecord
		in     Input
		reason PacingReason
		detail string
	}{
		{"no rows", nil, Input{ReviewsEnabled: true}, ReasonUnknown, ""},
		{"governor off", []bridge.TimelineRecord{running}, Input{}, ReasonGovernorOff, "routine reviews are disabled"},
		{"held", []bridge.TimelineRecord{running}, Input{ReviewsEnabled: true, Holds: []string{"interruption"}}, ReasonHeld, "interruption"},
		{"running", []bridge.TimelineRecord{running}, Input{ReviewsEnabled: true}, ReasonRunning, ""},
		{"refused", []bridge.TimelineRecord{running, refused}, Input{ReviewsEnabled: true}, ReasonRefused, "stale_facts, combat_plan, held by draft"},
		{"readmitted after a refusal", []bridge.TimelineRecord{refused, running}, Input{ReviewsEnabled: true}, ReasonRunning, ""},
		{"budget stop", []bridge.TimelineRecord{running, budget}, Input{ReviewsEnabled: true}, ReasonBudget, "the window spent its tick budget"},
		{"cinematic", []bridge.TimelineRecord{running, row("pacing_mode", 4, map[string]any{"mode": "cinematic"})}, Input{ReviewsEnabled: true}, ReasonCinematic, "cinematic"},
	} {
		now := Project(c.rows, c.in)
		if now.Pacing.Reason != c.reason || now.Pacing.Detail != c.detail {
			t.Fatal(c.name, now.Pacing)
		}
	}
	// A budget stop is not a reactive one.
	if now := Project([]bridge.TimelineRecord{budget}, Input{ReviewsEnabled: true}); now.Stops != (Counts{Stops: 1, Budget: 1}) {
		t.Fatal(now.Stops)
	}
}

// The panel bounds its goal rows and carries a prerequisite blocker's goal.
func TestProjectGoalBoundAndPrerequisite(t *testing.T) {
	progress := []policy.GoalProgress{{Goal: "MaintainResource-Steel", Method: "mine", Expected: "steel in storage", Blocked: policy.BlockedPrerequisite("MaintainFoodStorage")}}
	for i := 0; i < 10; i++ {
		progress = append(progress, policy.GoalProgress{Goal: policy.GoalID(string(rune('a' + i))), Method: "m", Expected: "e", NextReview: 100})
	}
	now := Project(nil, Input{Progress: progress, ReviewsEnabled: true})
	if len(now.Goals) != GoalsShown {
		t.Fatal(len(now.Goals))
	}
	if now.Goals[0].Blocked != "prerequisite:MaintainFoodStorage" || now.Goals[0].Prerequisite != "MaintainFoodStorage" {
		t.Fatal(now.Goals[0])
	}
	if now := Project(nil, Input{Progress: progress, ReviewsEnabled: true, Goals: 2}); len(now.Goals) != 2 {
		t.Fatal(len(now.Goals))
	}
}

// The wire shape a viewer reads: absent facts are null, never invented.
func TestProjectWireShapeWithoutAReview(t *testing.T) {
	encoded, err := json.Marshal(Project(nil, Input{ReviewsEnabled: true}))
	if err != nil {
		t.Fatal(err)
	}
	var wire map[string]any
	if err = json.Unmarshal(encoded, &wire); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"tick", "stage", "lastStop"} {
		if wire[key] != nil {
			t.Fatal(key, wire[key])
		}
	}
	goals, ok := wire["goals"].([]any)
	if !ok || len(goals) != 0 {
		t.Fatal(wire["goals"])
	}
	pacing, ok := wire["pacing"].(map[string]any)
	if !ok || pacing["reason"] != "unknown" || pacing["mode"] != ModeAutonomous || pacing["effectiveTps"] != float64(0) {
		t.Fatal(wire["pacing"])
	}
}
