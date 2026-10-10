package buildingruntime

import (
	"context"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// plannerRecordReason is what a planner's verdict files on its goal's
// record: the verdict's cause and its bounded subject. A refusal and a wait
// file their cause; a planner whose fight is running orders (combat_orders)
// or fell back to squad defense (hold_fallback) files its own wait cause,
// since the fight is under way and nothing failed; a planner that admitted or
// saw no deficit files the zero note, which clears the goal's filed refusal or
// wait; a switched-off planner files the opt-out hold. False skips filing: no
// verdict, an invalid one, or an outcome that says nothing about the goal (no
// review to judge, a stale proposal).
func plannerRecordReason(v Verdict) (policy.PlannerNote, bool) {
	if v.IsZero() {
		return policy.PlannerNote{}, false
	}
	if v.Validate() != nil {
		return policy.PlannerNote{}, false
	}
	switch v.Outcome {
	case OutcomeAdmitted, OutcomeNothingToDo:
		return policy.PlannerNote{}, true
	case OutcomeDisabled:
		return policy.PlannerNote{Cause: policy.CauseHeldOptIn}, true
	case OutcomeNoReview, OutcomeExpired:
		return policy.PlannerNote{}, false
	case OutcomeOrdersSent:
		return policy.PlannerNote{Cause: policy.CauseCombatOrders}, true
	case OutcomeHoldFallback:
		return policy.PlannerNote{Cause: policy.CauseHoldFallback}, true
	}
	return policy.PlannerNote{Cause: v.Refusal.Kind, Subject: policy.BoundSubject(v.Refusal.Subject)}, true
}

// noteRank orders the notes sibling planners file on one goal: a refusal
// outranks a wait, a wait outranks a clear (an enabled sibling that is
// idle), and a clear outranks the opt-out, which stands only when every
// planner of the goal is switched off.
func noteRank(n policy.PlannerNote) int {
	switch {
	case n.Cause == "":
		return 1
	case n.Cause == policy.CauseHeldOptIn:
		return 0
	case n.Cause.Waiting():
		return 2
	}
	return 3
}

// plannerReasonLog remembers the last note filed per goal; the idle-stall row
// lists them as the standing refusals. Only the step goroutine touches it
// (recordWave).
type plannerReasonLog struct {
	last map[policy.ConcernID]policy.PlannerNote
}

// changed files notes and returns those that differ from the last seen.
func (l *plannerReasonLog) changed(notes map[policy.ConcernID]policy.PlannerNote) map[policy.ConcernID]policy.PlannerNote {
	if l.last == nil {
		l.last = map[policy.ConcernID]policy.PlannerNote{}
	}
	out := map[policy.ConcernID]policy.PlannerNote{}
	for goal, note := range notes {
		if prior, seen := l.last[goal]; !seen || prior != note {
			l.last[goal] = note
			out[goal] = note
		}
	}
	return out
}

// wavePlannerReasons collects the goal notes of the planners that returned;
// siblings on one goal keep the highest-ranked note (noteRank).
func wavePlannerReasons(names []string, filing func(string) (policy.ConcernID, Verdict, bool)) map[policy.ConcernID]policy.PlannerNote {
	out := map[policy.ConcernID]policy.PlannerNote{}
	for _, name := range names {
		goal, verdict, ok := filing(name)
		if !ok {
			continue
		}
		note, ok := plannerRecordReason(verdict)
		if !ok {
			continue
		}
		if prior, seen := out[goal]; !seen || noteRank(note) > noteRank(prior) {
			out[goal] = note
		}
	}
	return out
}

// recordPlannerReasons notes each goal's planner refusal or wait and files it
// on the goal's progress record, so the status strip says why a goal has no
// method or what it waits on (the planner_step rows carry the same reasons).
func (s *ClockScheduler) recordPlannerReasons(call context.Context, wave *plannerWave) {
	notes := wavePlannerReasons(wave.finishedNames(), wave.filing)
	if len(notes) == 0 {
		return
	}
	s.plannerReasons.changed(notes)
	if s.player == nil || s.player.journal == nil {
		return
	}
	if _, err := s.player.journal.RecordPlannerReasons(call, notes); err != nil {
		plannerBookkeepingFailed(call, "reasons", err)
	}
}

// plannerBookkeepingFailed files the step's own failure to record a wave
// (the goal notes or the open work of the planners' kinds) as a failed
// planner_step row, so a store error is not swallowed.
func plannerBookkeepingFailed(ctx context.Context, target string, err error) {
	telemetry.Decide(ctx, telemetry.Decision{Kind: "planner_step", Component: "clock-scheduler", Level: slog.LevelWarn, Verdict: "failed", Reason: "journal_error", Target: target, Attrs: map[string]any{"error": err}})
}
