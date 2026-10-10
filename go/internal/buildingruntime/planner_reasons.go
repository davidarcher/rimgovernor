package buildingruntime

import (
	"context"
	"log/slog"
	"slices"

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

// plannerFiling is a goal's winning note with the outcome of the verdict that
// filed it (admitted and nothing_to_do both file the zero note).
type plannerFiling struct {
	Note    policy.PlannerNote
	Outcome Outcome
}

// plannerStanding is the note a goal stands on and the game tick it began
// standing on that cause.
type plannerStanding struct {
	Note  policy.PlannerNote
	Since int64
}

// concernTransition is one change of a goal's winning cause: the new cause
// with its subject and outcome, and the cause it replaced. HadPrevious is
// false for the first note seen since the process started (the past is
// unknown); Held is nil when the ticks the previous cause stood are unknown
// (the game clock went backwards, as a save load does).
type concernTransition struct {
	Concern     policy.ConcernID
	Filing      plannerFiling
	HadPrevious bool
	Previous    policy.Cause
	Held        *int64
}

// plannerReasonLog remembers the standing note per goal; the idle-stall row
// lists them as the standing refusals and changed turns a change of cause into
// a transition. It lives in memory only: after a restart the first note of a
// goal has no known past. Only the step goroutine touches it (recordWave).
type plannerReasonLog struct {
	last map[policy.ConcernID]plannerStanding
}

// changed files the wave's notes at tick and returns the goals whose cause
// changed, in goal order. A subject change under the same cause files
// silently, and a goal first seen clear is not a transition (nothing to explain).
func (l *plannerReasonLog) changed(filings map[policy.ConcernID]plannerFiling, tick int64) []concernTransition {
	if l.last == nil {
		l.last = map[policy.ConcernID]plannerStanding{}
	}
	goals := make([]policy.ConcernID, 0, len(filings))
	for goal := range filings {
		goals = append(goals, goal)
	}
	slices.Sort(goals)
	var out []concernTransition
	for _, goal := range goals {
		filing := filings[goal]
		prior, seen := l.last[goal]
		if seen && prior.Note.Cause == filing.Note.Cause {
			l.last[goal] = plannerStanding{Note: filing.Note, Since: prior.Since}
			continue
		}
		l.last[goal] = plannerStanding{Note: filing.Note, Since: tick}
		if !seen {
			if filing.Note.Cause != "" {
				out = append(out, concernTransition{Concern: goal, Filing: filing})
			}
			continue
		}
		t := concernTransition{Concern: goal, Filing: filing, HadPrevious: true, Previous: prior.Note.Cause}
		if held := tick - prior.Since; held >= 0 {
			t.Held = &held
		}
		out = append(out, t)
	}
	return out
}

// emitTransitions writes one concern_transition row per transition into the
// explanation ring: target the concern, verdict the filing verdict's outcome,
// reason the new cause (empty when it cleared), attrs subject, previous_reason
// and held_ticks (each only when known) and method, the goal's current method
// on its progress record.
func emitTransitions(call context.Context, transitions []concernTransition, methods map[policy.ConcernID]string) {
	for _, t := range transitions {
		attrs := map[string]any{"subject": t.Filing.Note.Subject, "method": methods[t.Concern]}
		if t.HadPrevious {
			attrs["previous_reason"] = string(t.Previous)
		}
		if t.Held != nil {
			attrs["held_ticks"] = *t.Held
		}
		telemetry.Decide(call, telemetry.Decision{Kind: telemetry.ConcernTransitionKind, Component: "clock-scheduler", Verdict: string(t.Filing.Outcome), Reason: string(t.Filing.Note.Cause), Target: string(t.Concern), Attrs: attrs})
	}
}

// wavePlannerReasons collects the goal filings of the planners that returned;
// siblings on one goal keep the highest-ranked note (noteRank).
func wavePlannerReasons(names []string, filing func(string) (policy.ConcernID, Verdict, bool)) map[policy.ConcernID]plannerFiling {
	out := map[policy.ConcernID]plannerFiling{}
	for _, name := range names {
		goal, verdict, ok := filing(name)
		if !ok {
			continue
		}
		note, ok := plannerRecordReason(verdict)
		if !ok {
			continue
		}
		if prior, seen := out[goal]; !seen || noteRank(note) > noteRank(prior.Note) {
			out[goal] = plannerFiling{Note: note, Outcome: verdict.Outcome}
		}
	}
	return out
}

// recordPlannerReasons notes each goal's planner refusal or wait and files it
// on the goal's progress record, so the status strip says why a goal has no
// method or what it waits on (the planner_step rows carry the same reasons).
// A goal whose cause changed also files a concern_transition row.
func (s *ClockScheduler) recordPlannerReasons(call context.Context, wave *plannerWave, tick int64) {
	filings := wavePlannerReasons(wave.finishedNames(), wave.filing)
	if len(filings) == 0 {
		return
	}
	transitions := s.plannerReasons.changed(filings, tick)
	var methods map[policy.ConcernID]string
	if s.player != nil && s.player.journal != nil {
		notes := make(map[policy.ConcernID]policy.PlannerNote, len(filings))
		for goal, f := range filings {
			notes[goal] = f.Note
		}
		var err error
		if _, methods, err = s.player.journal.RecordPlannerReasons(call, notes); err != nil {
			plannerBookkeepingFailed(call, "reasons", err)
		}
	}
	emitTransitions(call, transitions, methods)
}

// plannerBookkeepingFailed files the step's own failure to record a wave
// (the goal notes or the open work of the planners' kinds) as a failed
// planner_step row, so a store error is not swallowed.
func plannerBookkeepingFailed(ctx context.Context, target string, err error) {
	telemetry.Decide(ctx, telemetry.Decision{Kind: "planner_step", Component: "clock-scheduler", Level: slog.LevelWarn, Verdict: "failed", Reason: "journal_error", Target: target, Attrs: map[string]any{"error": err}})
}
