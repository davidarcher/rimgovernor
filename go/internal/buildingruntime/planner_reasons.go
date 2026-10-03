package buildingruntime

import (
	"context"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// plannerRecordReason is what a planner's verdict files on its goal's
// record. A refusal and a wait file their plain-English text (a wait marked
// as one); a planner that admitted, found work or saw no deficit files the
// zero note, which clears the goal's filed refusal or wait; a switched-off
// planner files the opt-out hold. False skips filing: no verdict, an invalid
// one, unprintable text, or an outcome that says nothing about the goal (no
// review to judge, a stale proposal).
func plannerRecordReason(v Verdict) (policy.PlannerNote, bool) {
	if v.IsZero() {
		return policy.PlannerNote{}, false
	}
	if err := v.Validate(); err != nil {
		clockSchedulerLog("planner verdict rejected: %v", err)
		return policy.PlannerNote{}, false
	}
	switch v.Outcome {
	case OutcomeAdmitted, OutcomeNothingToDo, OutcomeInProgress, OutcomeOrdersSent, OutcomeHoldFallback:
		return policy.PlannerNote{}, true
	case OutcomeDisabled:
		return policy.PlannerNote{Text: policy.PlannerOptOut}, true
	case OutcomeNoReview, OutcomeExpired:
		return policy.PlannerNote{}, false
	}
	s := v.Text()
	if len(s) > 96 {
		s = s[:96]
	}
	for i := 0; i < len(s); i++ {
		if s[i] < 0x20 || s[i] > 0x7e {
			return policy.PlannerNote{}, false
		}
	}
	return policy.PlannerNote{Text: s, Waiting: v.Outcome == OutcomeWaiting}, true
}

// noteRank orders the notes sibling planners file on one goal: a refusal
// outranks a wait, a wait outranks a clear (an enabled sibling that is
// idle), and a clear outranks the opt-out, which stands only when every
// planner of the goal is switched off.
func noteRank(n policy.PlannerNote) int {
	switch {
	case n.Text == "":
		return 1
	case n.Text == policy.PlannerOptOut && !n.Waiting:
		return 0
	case n.Waiting:
		return 2
	}
	return 3
}

// plannerReasonLog remembers the last note filed per goal so the service log
// names each goal's refusal or wait once per change. Only the step goroutine
// touches it (recordWave).
type plannerReasonLog struct {
	last map[policy.GoalID]policy.PlannerNote
}

// changed files notes and returns those that differ from the last seen.
func (l *plannerReasonLog) changed(notes map[policy.GoalID]policy.PlannerNote) map[policy.GoalID]policy.PlannerNote {
	if l.last == nil {
		l.last = map[policy.GoalID]policy.PlannerNote{}
	}
	out := map[policy.GoalID]policy.PlannerNote{}
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
func wavePlannerReasons(names []string, filing func(string) (policy.GoalID, Verdict, bool)) map[policy.GoalID]policy.PlannerNote {
	out := map[policy.GoalID]policy.PlannerNote{}
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

// recordPlannerReasons logs each goal's planner refusal or wait on change and
// files it on the goal's progress record, so the status strip and the
// service log say why a goal has no method or what it waits on.
func (s *ClockScheduler) recordPlannerReasons(call context.Context, wave *plannerWave) {
	notes := wavePlannerReasons(wave.finishedNames(), wave.filing)
	if len(notes) == 0 {
		return
	}
	for goal, note := range s.plannerReasons.changed(notes) {
		log := slog.Default().With(telemetry.ComponentKey, "clock-scheduler", "goal", string(goal))
		switch {
		case note.Text == "":
			log.Info("planner refusal cleared")
		case note.Waiting:
			log.Info("planner waiting", "reason", note.Text)
		case note.Text == policy.PlannerOptOut:
			log.Info("planner switched off")
		default:
			log.Info("planner refused", "reason", note.Text)
		}
	}
	if s.player == nil || s.player.journal == nil {
		return
	}
	if _, err := s.player.journal.RecordPlannerReasons(call, notes); err != nil {
		clockSchedulerLog("planner reasons: %v", err)
	}
}
