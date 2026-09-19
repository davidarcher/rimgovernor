package sustainedfood

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// FailFast ends a watch as soon as the journal shows the case cannot pass
// (#268), instead of running out the wall-clock ceiling and reporting the
// same refusal text the run notes say to read first. The zero value is on
// with the defaults below; a case where one of these shapes is an expected
// transient sets Disabled or raises the threshold.
//
// Three shapes abort, each with the journal's own text as the failure:
//   - no method: the routine review handed the watched goal a development
//     slot and its planner committed nothing (the row's Idle flag) for
//     NoMethodReviews consecutive reviews while the goal stayed
//     active/deficit with no method in flight;
//   - unsuccessful: an action of one of the goal's committed methods ended
//     unsuccessful for any reason but interrupted or cancelled (a pawn's
//     own needs or a cancelled order are re-planned; a native failure,
//     an expiry, a dead target or an unachieved outcome are not);
//   - refusal: the service's latest scheduler step still carries the same
//     isolated planner failure on a native refusal after RefusalSamples
//     consecutive samples (issue #219's shape: the same read refused every
//     step while the world under it cannot move).
type FailFast struct {
	Disabled bool
	// NoMethodReviews is how many consecutive reviews may leave the goal
	// idle with nothing in flight before the watch fails (default 5). A
	// review is one routine_review revision; unsampled revisions in
	// between do not count.
	NoMethodReviews int
	// RefusalSamples is how many consecutive samples the latest step may
	// stay a native refusal before the watch fails (default 6: 30 s at the
	// usual 5 s poll). The step event logs once per change of outcome, so
	// an unchanged latest line means the refusal repeated every step since.
	RefusalSamples int
}

const (
	defaultNoMethodReviews = 5
	defaultRefusalSamples  = 6
)

// Verdict is the report's record of a fail-fast abort (report["fail_fast"]).
type Verdict struct {
	Shape  string `json:"shape"`
	Reason string `json:"reason"`
	// Evidence is the journal text behind the verdict: the step line, the
	// unsuccessful action, or the review revisions counted.
	Evidence any `json:"evidence,omitempty"`
}

func (v Verdict) Error() string { return "fail-fast (" + v.Shape + "): " + v.Reason }

// failFastState folds the samples of one watch; check returns a Verdict
// the first time a shape completes.
type failFastState struct {
	cfg    FailFast
	goal   policy.GoalID
	stderr string

	lastRevision uint64
	idleReviews  []uint64

	lastStepLine string
	stepSamples  int
}

func newFailFast(cfg FailFast, goal policy.GoalID, stderrPath string) *failFastState {
	if cfg.NoMethodReviews <= 0 {
		cfg.NoMethodReviews = defaultNoMethodReviews
	}
	if cfg.RefusalSamples <= 0 {
		cfg.RefusalSamples = defaultRefusalSamples
	}
	return &failFastState{cfg: cfg, goal: goal, stderr: stderrPath}
}

// check folds one sample (SampleGoal's shape, without an error) and the
// service's latest scheduler step.
func (f *failFastState) check(sample map[string]any) (Verdict, bool) {
	if f == nil || f.cfg.Disabled {
		return Verdict{}, false
	}
	if v, ok := f.unsuccessful(sample); ok {
		return v, true
	}
	if v, ok := f.noMethod(sample); ok {
		return v, true
	}
	if f.stderr != "" {
		if step, ok := na.LastSchedulerStepFile(f.stderr); ok {
			return f.refusal(step)
		}
	}
	return Verdict{}, false
}

func (f *failFastState) unsuccessful(sample map[string]any) (Verdict, bool) {
	plans, _ := sample["plans"].([]map[string]any)
	for _, plan := range plans {
		rows, _ := plan["unsuccessful"].([]map[string]any)
		for _, row := range rows {
			reason := domain.UnsuccessfulReason(asString(row["reason"]))
			if reason == domain.NativeInterrupted || reason == domain.NativeCancelled {
				continue
			}
			return Verdict{
				Shape:    "unsuccessful",
				Reason:   fmt.Sprintf("goal %s plan %s action %s ended unsuccessful (%s)", f.goal, asString(plan["plan"]), asString(row["action"]), reason),
				Evidence: row,
			}, true
		}
	}
	return Verdict{}, false
}

func (f *failFastState) noMethod(sample map[string]any) (Verdict, bool) {
	revision, _ := sample["review_revision"].(uint64)
	if revision == 0 || revision == f.lastRevision {
		return Verdict{}, false
	}
	f.lastRevision = revision
	methodCount, _ := sample["method_count"].(int)
	development, _ := sample["development"].(map[string]any)
	idle, _ := development["idle"].(bool)
	committed, _ := development["committed"].(bool)
	deficit := asString(sample["need"]) == string(domain.NeedDeficit) && asString(sample["status"]) == string(domain.GoalActive)
	if !deficit || methodCount > 0 || committed || !idle {
		f.idleReviews = f.idleReviews[:0]
		return Verdict{}, false
	}
	f.idleReviews = append(f.idleReviews, revision)
	if len(f.idleReviews) < f.cfg.NoMethodReviews {
		return Verdict{}, false
	}
	return Verdict{
		Shape:    "no_method",
		Reason:   fmt.Sprintf("goal %s stayed active/deficit with no method through %d reviews that handed its planner the slot (revisions %d..%d, development reason %q)", f.goal, len(f.idleReviews), f.idleReviews[0], revision, asString(development["reason"])),
		Evidence: map[string]any{"revisions": append([]uint64(nil), f.idleReviews...), "development": development},
	}, true
}

func (f *failFastState) refusal(step na.SchedulerStep) (Verdict, bool) {
	if !step.Refused() {
		f.lastStepLine, f.stepSamples = "", 0
		return Verdict{}, false
	}
	if step.Line != f.lastStepLine {
		f.lastStepLine, f.stepSamples = step.Line, 0
	}
	f.stepSamples++
	if f.stepSamples < f.cfg.RefusalSamples {
		return Verdict{}, false
	}
	return Verdict{
		Shape:    "refusal",
		Reason:   fmt.Sprintf("the latest scheduler step has repeated the same native refusal for %d samples: %s", f.stepSamples, step.PlannerFailures),
		Evidence: step.Line,
	}, true
}
